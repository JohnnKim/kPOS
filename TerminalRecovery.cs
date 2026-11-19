using System.Text.Json;

namespace Kpos;

internal sealed record StoreTerminalProfile(Guid Id, string Name)
{
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kPOS", "store-terminal.json");
}
internal sealed record PendingStoreCheckout(Guid StoreId, string StoreUrl, StoreQuote Quote, StoreCommit Commit);

internal static class TerminalRecovery
{
    internal static T? Read<T>(string path) where T : class => !File.Exists(path) ? null :
        JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("The terminal recovery file is incomplete. Keep it for recovery.");

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using(var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

internal static class TerminalPairing
{
    internal static string Text(HomeTerminalHost host, bool store) =>
        $"Connection type: {(store ? "Store Terminal" : "Remote Connection")}\r\nStore address: {host.Url}\r\nCertificate SHA256: {host.Fingerprint}\r\nAccess key: {(store ? host.StoreToken : host.Token)}";

    internal static void Paste(string text, bool store, TextBox url, TextBox fingerprint, TextBox key)
    {
        var values = text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(": ", 2)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1].Trim());
        string expected = store ? "Store Terminal" : "Remote Connection";
        if(values.TryGetValue("Connection type", out string? role) && role != expected)
            throw new ArgumentException($"These details are for {role}. Copy {expected} pairing from Host Terminal.");
        if(!values.TryGetValue("Store address", out string? address) || !values.TryGetValue("Certificate SHA256", out string? pin) || !values.TryGetValue("Access key", out string? secret))
            throw new ArgumentException("Copy the complete pairing details from Host Terminal.");
        url.Text = address; fingerprint.Text = pin; key.Text = secret;
    }
}
