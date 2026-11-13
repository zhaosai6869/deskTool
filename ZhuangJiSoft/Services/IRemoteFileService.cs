using System.Collections.Generic;
using System.Threading.Tasks;

namespace DeskApp.Services;

/// <summary>
/// 远端文件操作抽象。electerm 的 SFTP(session-sftp.js) 与 FTP(ftp-client.js) 是两套实现，
/// 这里收敛成同一接口，让文件管理窗口不关心底层是 SFTP 还是 FTP。
/// </summary>
public interface IRemoteFileService : System.IAsyncDisposable
{
    string HomePath { get; }

    Task ConnectAsync();

    Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path);

    Task UploadAsync(string localPath, string remotePath);

    Task DownloadAsync(string remotePath, string localPath);

    Task DeleteAsync(string path, bool isDirectory);

    Task RenameAsync(string oldPath, string newPath);

    Task CreateDirectoryAsync(string path);

    string Combine(string dir, string name);

    string GetParent(string path);
}
