using System.IO;
using Microsoft.EntityFrameworkCore;
using DeskApp.Models;

namespace DeskApp.Data;

/// <summary>
/// EF Core 上下文。数据库文件落在 exe 同级 data\app.db，随软件走、可单独管控。
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<SoftwareItem> Items { get; set; } = null!;

    public DbSet<RdpRecord> RdpRecords { get; set; } = null!;

    public DbSet<SshRecord> SshRecords { get; set; } = null!;

    public DbSet<ToDeskRecord> ToDeskRecords { get; set; } = null!;

    public DbSet<SunflowerRecord> SunflowerRecords { get; set; } = null!;

    public DbSet<SphRecord> SphRecords { get; set; } = null!;

    public DbSet<DouyinRecord> DouyinRecords { get; set; } = null!;

    public DbSet<KuaishouRecord> KuaishouRecords { get; set; } = null!;

    public DbSet<BiliRecord> BiliRecords { get; set; } = null!;

    public DbSet<FanqieRecord> FanqieRecords { get; set; } = null!;

    public DbSet<LauncherMode> LauncherModes { get; set; } = null!;

    public DbSet<LauncherItem> LauncherItems { get; set; } = null!;

    public AppDbContext()
    {
        Database.EnsureCreated();
        // 兼容已存在的 app.db：EnsureCreated 不会给旧库补建新表，这里用 DDL 兜底
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS RdpRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL DEFAULT 3389,
                UserName TEXT,
                PasswordEncrypted TEXT,
                Notes TEXT,
                FullScreen INTEGER NOT NULL DEFAULT 0,
                AdminMode INTEGER NOT NULL DEFAULT 0,
                PromptCredentials INTEGER NOT NULL DEFAULT 0
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS SshRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL DEFAULT 22,
                UserName TEXT,
                PasswordEncrypted TEXT,
                Protocol TEXT NOT NULL DEFAULT 'SSH',
                Notes TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS ToDeskRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                PasswordEncrypted TEXT,
                Notes TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS SunflowerRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                DeviceCode TEXT NOT NULL,
                PasswordEncrypted TEXT,
                Notes TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS SphRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUrl TEXT NOT NULL,
                Title TEXT NOT NULL DEFAULT '',
                Author TEXT NOT NULL DEFAULT '',
                FileName TEXT NOT NULL DEFAULT '',
                Bytes INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT '排队中',
                Error TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                FinishedAt TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS DouyinRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUrl TEXT NOT NULL,
                Title TEXT NOT NULL DEFAULT '',
                Author TEXT NOT NULL DEFAULT '',
                FileName TEXT NOT NULL DEFAULT '',
                Bytes INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT '排队中',
                Error TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                FinishedAt TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS KuaishouRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUrl TEXT NOT NULL,
                Title TEXT NOT NULL DEFAULT '',
                Author TEXT NOT NULL DEFAULT '',
                FileName TEXT NOT NULL DEFAULT '',
                Bytes INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT '排队中',
                Error TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                FinishedAt TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS BiliRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUrl TEXT NOT NULL,
                Title TEXT NOT NULL DEFAULT '',
                Author TEXT NOT NULL DEFAULT '',
                FileName TEXT NOT NULL DEFAULT '',
                Bytes INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT '排队中',
                Error TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                FinishedAt TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS FanqieRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUrl TEXT NOT NULL,
                Title TEXT NOT NULL DEFAULT '',
                Author TEXT NOT NULL DEFAULT '',
                FileName TEXT NOT NULL DEFAULT '',
                Bytes INTEGER NOT NULL DEFAULT 0,
                ChapterDone INTEGER NOT NULL DEFAULT 0,
                ChapterTotal INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT '排队中',
                Error TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                FinishedAt TEXT
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS LauncherModes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL
            );");
        Database.ExecuteSqlRaw(
            @"CREATE TABLE IF NOT EXISTS LauncherItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ModeId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Target TEXT NOT NULL,
                Arguments TEXT NOT NULL DEFAULT ''
            );");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "app.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SoftwareItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.DownloadUrl).IsRequired(false);
        });
    }
}
