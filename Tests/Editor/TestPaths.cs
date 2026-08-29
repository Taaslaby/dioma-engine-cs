// Где лежат данные золотых следов: пакет может быть embedded или подключён
// по file:/git — путь спрашиваем у Unity Package Manager.

using System.IO;

public static class TestPaths
{
    public const string PackageName = "com.dioma.engine";

    public static string DataDir
    {
        get
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForPackageName(PackageName);
            string root = info != null && !string.IsNullOrEmpty(info.resolvedPath)
                ? info.resolvedPath
                : Path.GetFullPath(Path.Combine("Packages", PackageName));
            return Path.Combine(root, "Tests", "Editor", "Data");
        }
    }
}
