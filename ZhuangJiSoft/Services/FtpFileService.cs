using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentFTP;
using DeskApp.Models;

namespace DeskApp.Services;

/// <summary>
/// FTP / FTPS 实现（对齐 electerm 的 ftp-client.js：basic-ftp 的 access/uploadFrom/downloadTo）。
/// EncryptionMode = Auto 自动协商明文 FTP 与显式 FTPS，端口仍为记录里的 Port。
/// </summary>
public sealed class FtpFileService : IRemoteFileService
{
    private readonly SshConnectionInfo _info;
    private AsyncFtpClient? _client;

    public FtpFileService(SshConnectionInfo info) => _info = info;

    public string HomePath => "/";

    public async Task ConnectAsync()
    {
        var config = new FtpConfig
        {
            EncryptionMode = FtpEncryptionMode.Auto,
            ValidateAnyCertificate = true
        };
        _client = new AsyncFtpClient(_info.Host, _info.UserName, _info.Password, _info.Port, config);
        await _client.Connect();
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path)
    {
        EnsureConnected();
        var items = await _client!.GetListing(Normalize(path));
        return items
            .Where(f => f.Name != "." && f.Name != "..")
            .Select(ToEntry)
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name)
            .ToList();
    }

    public async Task UploadAsync(string localPath, string remotePath)
    {
        EnsureConnected();
        await _client!.UploadFile(localPath, Normalize(remotePath), FtpRemoteExists.Overwrite, true);
    }

    public async Task DownloadAsync(string remotePath, string localPath)
    {
        EnsureConnected();
        await _client!.DownloadFile(localPath, Normalize(remotePath), FtpLocalExists.Overwrite);
    }

    public async Task DeleteAsync(string path, bool isDirectory)
    {
        EnsureConnected();
        var p = Normalize(path);
        if (isDirectory) await _client!.DeleteDirectory(p);
        else await _client!.DeleteFile(p);
    }

    public async Task RenameAsync(string oldPath, string newPath)
    {
        EnsureConnected();
        await _client!.Rename(Normalize(oldPath), Normalize(newPath));
    }

    public async Task CreateDirectoryAsync(string path)
    {
        EnsureConnected();
        await _client!.CreateDirectory(Normalize(path));
    }

    public string Combine(string dir, string name)
    {
        var d = Normalize(dir);
        return d == "/" ? "/" + name : d + "/" + name;
    }

    public string GetParent(string path)
    {
        var p = Normalize(path);
        if (p == "/") return "/";
        int idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    private static string Normalize(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');

    private static RemoteFileEntry ToEntry(FtpListItem f)
    {
        var isDir = f.Type == FtpObjectType.Directory;
        return new RemoteFileEntry
        {
            Name = f.Name,
            FullPath = f.FullName,
            IsDirectory = isDir,
            Size = f.Size,
            Modified = f.Modified,
            Permissions = isDir ? "drwxr-xr-x" : "-rw-r--r--"
        };
    }

    private void EnsureConnected()
    {
        if (_client is null || !_client.IsConnected)
        {
            throw new InvalidOperationException("FTP 连接已断开，请重新打开文件管理窗口。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try { await _client.DisposeAsync(); } catch { }
        }
    }
}
