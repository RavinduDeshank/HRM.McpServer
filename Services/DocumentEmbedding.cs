using System.Text.RegularExpressions;

namespace HR.McpServer;

public static class DocumentEmbedding
{
    private const int VectorSize = 256;
    private const int MaxChunkChars = 800;

    // Splits text into paragraph-sized parts.
    public static List<string> ChunkText(string text)
    {
        var paragraphs = Regex.Split(text, @"\r?\n\s*\r?\n")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (paragraphs.Count == 0)
        {
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text.Trim() };
        }

        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + paragraph.Length > MaxChunkChars)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.AppendLine(paragraph);
            current.AppendLine();
        }
        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
        }

        return chunks;
    }

    // Feature-hashing vectorizer: term counts hashed into a fixed-size vector, then L2-normalized.
    public static float[] Embed(string text)
    {
        var vector = new float[VectorSize];
        foreach (var token in Tokenize(text))
        {
            var index = (token.GetHashCode() & int.MaxValue) % VectorSize;
            vector[index] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value);

    // Dot product of two normalized vectors == cosine similarity.
    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }

    public static string SerializeVector(float[] vector) => string.Join(',', vector);

    public static float[] DeserializeVector(string serialized) =>
        serialized.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(float.Parse).ToArray();
}
