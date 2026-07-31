namespace Nullprice.Sheaf.Core.Tests;

public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sheaf-tests", Guid.NewGuid().ToString("n"));

    public string In => Ensure(Path.Combine(Root, "in"));
    public string Out => Ensure(Path.Combine(Root, "out"));

    public string AddPdf(string relativePath, byte[] bytes)
    {
        var full = Path.Combine(In, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch { }
    }
}
