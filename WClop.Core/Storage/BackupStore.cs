namespace WClop.Core.Storage;

/// <summary>
/// Copies originals into the backups folder before anything destructive happens (project.md §14.2).
/// Backups are named with a content hash so different files with the same name don't collide,
/// and identical content is only stored once.
/// </summary>
public sealed class BackupStore(AppPaths paths)
{
    private const int MaxStemLength = 60;

    public string PathFor(string sourcePath, string contentHash)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (stem.Length > MaxStemLength)
            stem = stem[..MaxStemLength];
        return Path.Combine(paths.Backups, $"{stem}-{contentHash[..16]}{Path.GetExtension(sourcePath)}");
    }

    /// <summary>Returns the backup path, copying the file there first if it isn't already backed up.</summary>
    public string Backup(string sourcePath, string contentHash)
    {
        var backupPath = PathFor(sourcePath, contentHash);
        if (File.Exists(backupPath))
            return backupPath;

        Directory.CreateDirectory(paths.Backups);
        // Copy to a temp name then rename, so a crash can't leave a half-written backup under the real name.
        var tempPath = AppPaths.NewTempPath(paths.Backups, ".partial");
        File.Copy(sourcePath, tempPath);
        try
        {
            File.Move(tempPath, backupPath);
        }
        catch (IOException) when (File.Exists(backupPath))
        {
            // Another job backed up the same content first.
            File.Delete(tempPath);
        }

        return backupPath;
    }
}
