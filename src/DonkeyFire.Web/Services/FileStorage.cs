using DonkeyFire.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Services;

/// <summary>
/// Stores uploads on local disk under Storage:Root (default App_Data/uploads).
/// Receipts and other uploads; on Fly.io this is the /data volume.
/// </summary>
public class FileStorage(IDbContextFactory<AppDbContext> dbf, IConfiguration config, IWebHostEnvironment env)
{
    public const long MaxUploadBytes = 25L * 1024 * 1024;

    private string Root
    {
        get
        {
            var configured = config["Storage:Root"];
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(env.ContentRootPath, "App_Data", "uploads")
                : configured;
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public async Task<StoredFile> SaveAsync(int businessId, Stream content, string fileName, string? contentType, int? userId)
    {
        var safeName = Path.GetFileName(fileName);
        var ext = Path.GetExtension(safeName).ToLowerInvariant();
        var relative = Path.Combine(businessId.ToString(), $"{Guid.NewGuid():N}{ext}");
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        long size;
        await using (var fs = File.Create(full))
        {
            await content.CopyToAsync(fs);
            size = fs.Length;
        }

        var file = new StoredFile
        {
            BusinessId = businessId,
            FileName = safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? GuessContentType(ext) : contentType,
            Size = size,
            StoragePath = relative,
            UploadedById = userId,
        };
        await using var db = await dbf.CreateDbContextAsync();
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    public string GetFullPath(StoredFile file) => Path.Combine(Root, file.StoragePath);

    public async Task<byte[]> ReadAllBytesAsync(StoredFile file) => await File.ReadAllBytesAsync(GetFullPath(file));

    public async Task DeleteAsync(int fileId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var file = await db.Files.FindAsync(fileId);
        if (file is null) return;
        var full = GetFullPath(file);
        if (File.Exists(full)) File.Delete(full);
        db.Files.Remove(file);
        await db.SaveChangesAsync();
    }

    public static string GuessContentType(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".dwg" => "application/acad",
        _ => "application/octet-stream",
    };

    public static bool IsImage(string? contentType) => contentType?.StartsWith("image/") == true;
    public static bool IsPdf(string? contentType) => contentType == "application/pdf";
}
