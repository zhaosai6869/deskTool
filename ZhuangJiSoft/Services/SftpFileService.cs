using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using DeskApp.Models;

namespace DeskApp.Services;

/// <summary>
/// SFTP 实现（对齐 electerm 的 session-sftp.js：复用同一 SSH 连接开 sftp 子系统）。
/// 这里用 SSH.NET 的 SftpClient 专用连接，避免与终端窗口抢 ShellStream。
/// </summary>
public sealed class SftpFileService : IRemoteFileService
{
    private readonly SshConnectionInfo _info;
    private SftpClient? _client;

    public SftpFileService(SshConnectionInfo info) => _info = info;

    public string HomePath => "/";

    public async Task ConnectAsync()
    {
        var auth = new PasswordAuthenticationMethod(_info.UserName, _info.Password);
        var conn = new ConnectionInfo(_info.Host, _info.Port, _info.UserName, auth)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _client = new SftpClient(conn);
        await Task.Run(() => _client.Connect());
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path)
    {
        EnsureConnected();
        return await Task.Run(() =>
            _client!.ListDirectory(Normalize(path))
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(ToEntry)
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name)
                .ToList());
    }

    public async Task UploadAsync(string localPath, string remotePath)
    {
        EnsureConnected();
        await Task.Run(() =>
        {
            using var fs = File.OpenRead(localPath);
            _client!.UploadFile(fs, Normalize(remotePath), true);
        });
    }

    public async Task DownloadAsync(string remotePath, string localPath)
    {
        EnsureConnected();
        await Task.Run(() =>
        {
            using var fs = File.Create(localPath);
            _client!.DownloadFile(Normalize(remotePath), fs);
        });
    }

    public async Task DeleteAsync(string path, bool isDirectory)
    {
        EnsureConnected();
        await Task.Run(() =>
        {
            var p = Normalize(path);
            if (isDirectory) _client!.DeleteDirectory(p);
            else _client!.DeleteFile(p);
        });
    }

    public async Task RenameAsync(string oldPath, string newPath)
    {
        EnsureConnected();
        await Task.Run(() => _client!.RenameFile(Normalize(oldPath), Normalize(newPath)));
    }

    public async Task CreateDirectoryAsync(string path)
    {
        EnsureConnected();
        await Task.Run(() => _client!.CreateDirectory(Normalize(path)));
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

    private static RemoteFileEntry ToEntry(ISftpFile f) => new()
    {
        Name = f.Name,
        FullPath = f.FullName,
        IsDirectory = f.IsDirectory,
        Size = f.IsDirectory ? 0 : f.Length,
        Modified = f.LastWriteTime,
        Permissions = f.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--"
    };

    private void EnsureConnected()
    {
        if (_client is null || !_client.IsConnected)
        {
            throw new InvalidOperationException("SFTP 连接已断开，请重新打开文件管理窗口。");
        }
    }

    public ValueTask DisposeAsync()
    {
        try { if (_client?.IsConnected == true) _client.Disconnect(); } catch { }
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
