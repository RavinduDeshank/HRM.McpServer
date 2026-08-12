using System.Text.Json;
using McpDotNet;
using McpDotNet.Protocol.Transport;
using McpDotNet.Protocol.Types;
using McpDotNet.Server;
using Microsoft.EntityFrameworkCore;
using HR.McpServer;

// Stdio MCP server.
class Program
{
    // Splits a Document's content into embedded DocumentChunk rows, ready to save.
    static List<DocumentChunk> BuildChunks(Document document)
    {
        return DocumentEmbedding.ChunkText(document.Content)
            .Select(text => new DocumentChunk
            {
                DocumentId = document.Id,
                Type = document.Type,
                EmployeeId = document.EmployeeId,
                FileName = document.FileName,
                Text = text,
                Vector = DocumentEmbedding.SerializeVector(DocumentEmbedding.Embed(text))
            })
            .ToList();
    }

    static async Task Main(string[] args)
    {
        using (var db = new EmployeeDbContext())
        {
            // schema only for new database.
            db.Database.EnsureCreated();
            // If Documents table not exist.
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Documents')
                BEGIN
                    CREATE TABLE [Documents] (
                        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                        [Type] nvarchar(max) NOT NULL,
                        [EmployeeId] nvarchar(max) NULL,
                        [FileName] nvarchar(max) NOT NULL,
                        [Content] nvarchar(max) NOT NULL,
                        [UploadedAtUtc] datetime2 NOT NULL
                    );
                END");

            // If DocumentChunks table (RAG vector index) not exist.
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DocumentChunks')
                BEGIN
                    CREATE TABLE [DocumentChunks] (
                        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                        [DocumentId] uniqueidentifier NOT NULL,
                        [Type] nvarchar(max) NOT NULL,
                        [EmployeeId] nvarchar(max) NULL,
                        [FileName] nvarchar(max) NOT NULL,
                        [Text] nvarchar(max) NOT NULL,
                        [Vector] nvarchar(max) NOT NULL
                    );
                END");

            // Sample Data
            if (!db.Employees.Any())
            {
                db.Employees.AddRange(
                    new Employee { EmployeeId = "EMP001", Name = "Alice", Status = "Probation", CasualLeaveBalance = 0, SickLeaveBalance = 2, AnnualLeaveBalance = 0 },
                    new Employee { EmployeeId = "EMP002", Name = "Bob", Status = "Permanent", CasualLeaveBalance = 2, SickLeaveBalance = 5, AnnualLeaveBalance = 8 }
                );
                db.SaveChanges();
            }

            // Backfill: chunk+embed any already-uploaded document that has no chunks yet.
            var chunkedDocIds = db.DocumentChunks.Select(c => c.DocumentId).Distinct().ToHashSet();
            var unchunkedDocs = db.Documents.Where(d => !chunkedDocIds.Contains(d.Id)).ToList();
            foreach (var doc in unchunkedDocs)
            {
                db.DocumentChunks.AddRange(BuildChunks(doc));
            }
            if (unchunkedDocs.Count > 0)
            {
                db.SaveChanges();
            }
        }

        // MCP server setup
        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = "HR_Data_Server", Version = "1.0.0" },
            Capabilities = new()
            {
                Tools = new()
                {
                    // available tools and inputs.
                    ListToolsHandler = async (request, cancellationToken) =>
                    {
                        return new ListToolsResult
                        {
                            Tools = new List<Tool>
                            {
                                new Tool
                                {
                                    Name = "GetLeaveBalance",
                                    Description = "Fetches the current leave balances and employment status for a specific employee.",
                                    InputSchema = new JsonSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, JsonSchemaProperty>
                                        {
                                            ["employeeId"] = new JsonSchemaProperty { Type = "string", Description = "The unique ID of the employee (e.g., EMP001)" }
                                        },
                                        Required = new List<string> { "employeeId" }
                                    }
                                },
                                new Tool
                                {
                                    Name = "UploadDocument",
                                    Description = "Persists an uploaded HR document (policy or contract) and its extracted text content to the database.",
                                    InputSchema = new JsonSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, JsonSchemaProperty>
                                        {
                                            ["type"] = new JsonSchemaProperty { Type = "string", Description = "Document type: 'Policy' or 'Contract'" },
                                            ["fileName"] = new JsonSchemaProperty { Type = "string", Description = "Original file name of the uploaded document" },
                                            ["content"] = new JsonSchemaProperty { Type = "string", Description = "Extracted plain-text content of the document" },
                                            ["employeeId"] = new JsonSchemaProperty { Type = "string", Description = "Employee ID this document belongs to (required for Contract documents)" }
                                        },
                                        Required = new List<string> { "type", "fileName", "content" }
                                    }
                                },
                                new Tool
                                {
                                    Name = "ListDocuments",
                                    Description = "Lists all uploaded HR documents (metadata only, no content).",
                                    InputSchema = new JsonSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, JsonSchemaProperty>()
                                    }
                                },
                                new Tool
                                {
                                    Name = "GetGlobalPolicies",
                                    Description = "Fetches all global HR policy documents, including their content.",
                                    InputSchema = new JsonSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, JsonSchemaProperty>()
                                    }
                                },
                                new Tool
                                {
                                    Name = "DeleteDocument",
                                    Description = "Deletes an uploaded HR document by its ID.",
                                    InputSchema = new JsonSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, JsonSchemaProperty>
                                        {
                                            ["id"] = new JsonSchemaProperty { Type = "string", Description = "The ID (GUID) of the document to delete" }
                                        },
                                        Required = new List<string> { "id" }
                                    }
                                },
                                new Tool
                                {
                                    Name = "SearchDocuments",
                                    Description = "RAG retrieval: returns the top-matching document chunks (by embedding similarity) for a query, scoped to global policies plus one employee's own contracts.",
                                    InputSchema = new JsonSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, JsonSchemaProperty>
                                        {
                                            ["query"] = new JsonSchemaProperty { Type = "string", Description = "The question/text to find relevant document chunks for" },
                                            ["employeeId"] = new JsonSchemaProperty { Type = "string", Description = "Employee ID, used to scope which contract chunks are eligible" },
                                            ["topK"] = new JsonSchemaProperty { Type = "number", Description = "Max number of chunks to return (default 6)" }
                                        },
                                        Required = new List<string> { "query" }
                                    }
                                },
                                new Tool
                                {
                                    Name = "GetContractsForEmployee",
                                    Description = "Fetches the contract document(s) for a specific employee, including their content.",
                                    InputSchema = new JsonSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, JsonSchemaProperty>
                                        {
                                            ["employeeId"] = new JsonSchemaProperty { Type = "string", Description = "The unique ID of the employee (e.g., EMP001)" }
                                        },
                                        Required = new List<string> { "employeeId" }
                                    }
                                }
                            }
                        };
                    },

                    // Call handler.
                    CallToolHandler = async (request, cancellationToken) =>
                    {
                        // Convert to JSON response.
                        static CallToolResponse JsonResponse(object payload) => new CallToolResponse
                        {
                            Content = new List<Content>
                            {
                                new Content
                                {
                                    Type = "application/json",
                                    Text = JsonSerializer.Serialize(payload)
                                }
                            }
                        };

                        // Document DTO
                        static object ToDto(Document d) => new
                        {
                            id = d.Id,
                            fileName = d.FileName,
                            type = d.Type,
                            employeeId = d.EmployeeId,
                            uploadedAtUtc = d.UploadedAtUtc
                        };

                        // Document DTO with data.
                        static object ToDtoWithContent(Document d) => new
                        {
                            id = d.Id,
                            fileName = d.FileName,
                            type = d.Type,
                            employeeId = d.EmployeeId,
                            content = d.Content,
                            uploadedAtUtc = d.UploadedAtUtc
                        };

                        // Get leave balance by emp id.
                        if (request.Params?.Name == "GetLeaveBalance")
                        {
                            if (request.Params.Arguments?.TryGetValue("employeeId", out var employeeId) is not true || employeeId is null)
                            {
                                throw new McpServerException("Missing required argument 'employeeId'");
                            }

                            string empId = employeeId.ToString() ?? string.Empty;

                            await using var db = new EmployeeDbContext();
                            var employee = await db.Employees
                                .FirstOrDefaultAsync(e => e.EmployeeId.ToUpper() == empId.ToUpper(), cancellationToken);

                            if (employee is null)
                            {
                                return JsonResponse(new { error = $"Employee with ID {empId} not found." });
                            }

                            return JsonResponse(new
                            {
                                employeeId = employee.EmployeeId,
                                name = employee.Name,
                                status = employee.Status,
                                casualLeave = employee.CasualLeaveBalance,
                                sickLeave = employee.SickLeaveBalance,
                                annualLeave = employee.AnnualLeaveBalance
                            });
                        }

                        // Upload a new document and extract data.
                        if (request.Params?.Name == "UploadDocument")
                        {
                            var args = request.Params.Arguments;
                            if (args?.TryGetValue("type", out var type) is not true || type is null ||
                                args?.TryGetValue("fileName", out var fileName) is not true || fileName is null ||
                                args?.TryGetValue("content", out var content) is not true || content is null)
                            {
                                throw new McpServerException("Missing required argument: 'type', 'fileName', and 'content' are required.");
                            }
                            args.TryGetValue("employeeId", out var employeeIdArg);

                            var document = new Document
                            {
                                Type = type.ToString() ?? string.Empty,
                                FileName = fileName.ToString() ?? string.Empty,
                                Content = content.ToString() ?? string.Empty,
                                EmployeeId = employeeIdArg?.ToString()
                            };

                            await using var db = new EmployeeDbContext();
                            db.Documents.Add(document);
                            db.DocumentChunks.AddRange(BuildChunks(document));
                            await db.SaveChangesAsync(cancellationToken);

                            return JsonResponse(ToDto(document));
                        }

                        // Get all documents
                        if (request.Params?.Name == "ListDocuments")
                        {
                            await using var db = new EmployeeDbContext();
                            var documents = await db.Documents.ToListAsync(cancellationToken);
                            return JsonResponse(documents.Select(ToDto));
                        }

                        // Returns all global Policy documents.
                        if (request.Params?.Name == "GetGlobalPolicies")
                        {
                            await using var db = new EmployeeDbContext();
                            var policies = await db.Documents
                                .Where(d => d.Type == "Policy")
                                .ToListAsync(cancellationToken);
                            return JsonResponse(policies.Select(ToDtoWithContent));
                        }

                        // Deletes a document by id.
                        if (request.Params?.Name == "DeleteDocument")
                        {
                            if (request.Params.Arguments?.TryGetValue("id", out var idArg) is not true || idArg is null ||
                                !Guid.TryParse(idArg.ToString(), out var docId))
                            {
                                throw new McpServerException("Missing or invalid required argument 'id'");
                            }

                            await using var db = new EmployeeDbContext();
                            var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId, cancellationToken);
                            if (document is null)
                            {
                                return JsonResponse(new { error = $"Document with ID {docId} not found." });
                            }

                            var chunksToRemove = db.DocumentChunks.Where(c => c.DocumentId == docId);
                            db.DocumentChunks.RemoveRange(chunksToRemove);
                            db.Documents.Remove(document);
                            await db.SaveChangesAsync(cancellationToken);

                            return JsonResponse(new { success = true, id = docId });
                        }

                        // RAG retrieval: embeds the query, ranks chunks by cosine similarity, returns the top matches.
                        if (request.Params?.Name == "SearchDocuments")
                        {
                            if (request.Params.Arguments?.TryGetValue("query", out var queryArg) is not true || queryArg is null)
                            {
                                throw new McpServerException("Missing required argument 'query'");
                            }
                            string query = queryArg.ToString() ?? string.Empty;

                            string? empIdUpper = null;
                            if (request.Params.Arguments.TryGetValue("employeeId", out var empIdArg) && empIdArg is not null)
                            {
                                empIdUpper = empIdArg.ToString()?.ToUpper();
                            }

                            int topK = 6;
                            if (request.Params.Arguments.TryGetValue("topK", out var topKArg) && topKArg is not null
                                && int.TryParse(topKArg.ToString(), out var parsedTopK))
                            {
                                topK = parsedTopK;
                            }

                            await using var db = new EmployeeDbContext();
                            var candidates = await db.DocumentChunks
                                .Where(c => c.Type == "Policy" ||
                                    (c.Type == "Contract" && empIdUpper != null && c.EmployeeId != null && c.EmployeeId.ToUpper() == empIdUpper))
                                .ToListAsync(cancellationToken);

                            var queryVector = DocumentEmbedding.Embed(query);
                            var topMatches = candidates
                                .Select(c => new { Chunk = c, Score = DocumentEmbedding.CosineSimilarity(queryVector, DocumentEmbedding.DeserializeVector(c.Vector)) })
                                .OrderByDescending(m => m.Score)
                                .Take(topK)
                                .Select(m => new
                                {
                                    fileName = m.Chunk.FileName,
                                    type = m.Chunk.Type,
                                    employeeId = m.Chunk.EmployeeId,
                                    text = m.Chunk.Text,
                                    score = m.Score
                                });

                            return JsonResponse(topMatches);
                        }

                        // Returns Contract-type policy documents by emp id.
                        if (request.Params?.Name == "GetContractsForEmployee")
                        {
                            if (request.Params.Arguments?.TryGetValue("employeeId", out var contractEmployeeId) is not true || contractEmployeeId is null)
                            {
                                throw new McpServerException("Missing required argument 'employeeId'");
                            }
                            string contractEmpId = contractEmployeeId.ToString() ?? string.Empty;

                            await using var db = new EmployeeDbContext();
                            var contracts = await db.Documents
                                .Where(d => d.Type == "Contract" && d.EmployeeId != null && d.EmployeeId.ToUpper() == contractEmpId.ToUpper())
                                .ToListAsync(cancellationToken);
                            return JsonResponse(contracts.Select(ToDtoWithContent));
                        }

                        throw new McpServerException($"Unknown tool: '{request.Params?.Name}'");
                    }
                }
            }
        };

        // Start the server and wait.
        await using IMcpServer server = McpServerFactory.Create(new StdioServerTransport("HR_Data_Server"), options);
        await server.StartAsync();

        await Task.Delay(Timeout.Infinite);
    }
}
