using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using StowCrate.Application.LocalState;

namespace StowCrate.Archiving.SevenZip;

public sealed record SevenZipProcessRequest(string ExecutablePath, string? WorkingDirectory,
    IReadOnlyList<string> Arguments, SecretMaterialLease? Password);
public sealed record SevenZipProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed record SevenZipBinaryProcessResult(int ExitCode, ReadOnlyMemory<byte> StandardOutput, string StandardError);

public sealed class SevenZipProcessRunner(int outputLimitBytes = 64 * 1024)
{
    public async Task<SevenZipProcessResult> RunAsync(SevenZipProcessRequest request, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(request.ExecutablePath) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = request.Password is not null, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
        if (request.WorkingDirectory is not null) start.WorkingDirectory = request.WorkingDirectory;
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("7-Zip process did not start.");
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, outputLimitBytes, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, outputLimitBytes, cancellationToken);
            if (request.Password is not null)
            {
                await ArchivePasswordEncoding.WriteLineAsync(process.StandardInput.BaseStream, request.Password.Material, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SevenZipBinaryProcessResult> RunBinaryAsync(SevenZipProcessRequest request, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(request.ExecutablePath) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = request.Password is not null, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
        if (request.WorkingDirectory is not null) start.WorkingDirectory = request.WorkingDirectory;
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start }; process.Start();
        try
        {
            var output = new MemoryStream(); var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var error = ReadBoundedAsync(process.StandardError, outputLimitBytes, cancellationToken);
            if (request.Password is not null) { await ArchivePasswordEncoding.WriteLineAsync(process.StandardInput.BaseStream, request.Password.Material, cancellationToken).ConfigureAwait(false); process.StandardInput.Close(); }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); await copy.ConfigureAwait(false);
            if (output.Length > outputLimitBytes) throw new InvalidOperationException("7-Zip binary output exceeded the configured limit.");
            return new(process.ExitCode, output.ToArray(), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(limit, 4096));
        try
        {
            var builder = new StringBuilder(); int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit)), token).ConfigureAwait(false)) > 0)
                if (builder.Length < limit) builder.Append(buffer, 0, Math.Min(read, limit - builder.Length));
            return builder.ToString();
        }
        finally { Array.Clear(buffer); ArrayPool<char>.Shared.Return(buffer); }
    }
}

public static class ArchivePasswordEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static async Task WriteLineAsync(Stream target, ReadOnlyMemory<byte> material, CancellationToken token)
    {
        if (material.IsEmpty || material.Length > 1024 || material.Span.IndexOfAny((byte)0, (byte)'\r', (byte)'\n') >= 0)
            throw new ArgumentException("Password material cannot be empty, exceed 1024 UTF-8 bytes, or contain NUL/CR/LF.", nameof(material));
        _ = StrictUtf8.GetCharCount(material.Span);
        var temporary = ArrayPool<byte>.Shared.Rent(material.Length + 1);
        try
        {
            material.Span.CopyTo(temporary); temporary[material.Length] = (byte)'\n';
            await target.WriteAsync(temporary.AsMemory(0, material.Length + 1), token).ConfigureAwait(false);
            await target.FlushAsync(token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(temporary); ArrayPool<byte>.Shared.Return(temporary); }
    }
}
