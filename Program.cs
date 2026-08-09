using System.Text.Json;
using McpDotNet;
using McpDotNet.Protocol.Transport;
using McpDotNet.Protocol.Types;
using McpDotNet.Server;
using Microsoft.EntityFrameworkCore;
using HR.McpServer;

class Program
{
    static async Task Main(string[] args)
    {
        using (var db = new EmployeeDbContext())
        {
            db.Database.EnsureCreated();

            if (!db.Employees.Any())
            {
                db.Employees.AddRange(
                    new Employee { EmployeeId = "EMP001", Name = "Alice", Status = "Probation", CasualLeaveBalance = 0, SickLeaveBalance = 2, AnnualLeaveBalance = 0 },
                    new Employee { EmployeeId = "EMP002", Name = "Bob", Status = "Permanent", CasualLeaveBalance = 2, SickLeaveBalance = 5, AnnualLeaveBalance = 8 }
                );
                db.SaveChanges();
            }
        }

        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = "HR_Data_Server", Version = "1.0.0" },
            Capabilities = new()
            {
                Tools = new()
                {
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
                                }
                            }
                        };
                    },

                    CallToolHandler = async (request, cancellationToken) =>
                    {
                        if (request.Params?.Name != "GetLeaveBalance")
                        {
                            throw new McpServerException($"Unknown tool: '{request.Params?.Name}'");
                        }

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
                            return new CallToolResponse
                            {
                                Content = new List<Content>
                                {
                                    new Content
                                    {
                                        Type = "application/json",
                                        Text = JsonSerializer.Serialize(new { error = $"Employee with ID {empId} not found." })
                                    }
                                }
                            };
                        }

                        var payload = new
                        {
                            employeeId = employee.EmployeeId,
                            name = employee.Name,
                            status = employee.Status,
                            casualLeave = employee.CasualLeaveBalance,
                            sickLeave = employee.SickLeaveBalance,
                            annualLeave = employee.AnnualLeaveBalance
                        };

                        return new CallToolResponse
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
                    }
                }
            }
        };

        await using IMcpServer server = McpServerFactory.Create(new StdioServerTransport("HR_Data_Server"), options);
        await server.StartAsync();

        await Task.Delay(Timeout.Infinite);
    }
}
