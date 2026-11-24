using MyBackup.Core.Config;
using MyBackup.Core.Sources;

namespace MyBackup.Core.Tests;

public class ProfileFilterTests
{
    private const string User = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Theory]
    [InlineData("S-1-5-18", @"C:\Windows\system32\config\systemprofile", false)]
    [InlineData("S-1-5-19", @"C:\Windows\ServiceProfiles\LocalService", false)]
    [InlineData("S-1-5-80-1234", @"C:\Users\MSSQLSERVER", false)]
    [InlineData(User, @"C:\Users\ivanov", true)]
    [InlineData(User + ".bak", @"C:\Users\ivanov", false)]
    [InlineData(User, @"C:\Users\TEMP", false)]
    [InlineData(User, @"C:\Users\TEMP.DOMAIN.001", false)]
    [InlineData(User, @"C:\Users\Administrator", false)]
    [InlineData(User, @"C:\Users\administrator.CORP", false)]
    public void FiltersSystemAndExcludedProfiles(string sid, string path, bool expected) =>
        Assert.Equal(expected, ProfileFilter.IsRegularUser(sid, path, ["Administrator"]));
}

public class KnownFoldersTests
{
    private static readonly UserProfile Loaded = new("S-1-5-21-1", "ivanov.CORP", @"C:\Users\ivanov.CORP", true);

    [Fact]
    public void UsesRedirectionWhenHiveLoaded()
    {
        var path = KnownFolders.Resolve(Loaded, "Documents",
            (_, value) => value == "Personal" ? @"D:\Redirect\%USERNAME%\Docs" : null);
        Assert.Equal(@"D:\Redirect\ivanov\Docs", path);
    }

    [Fact]
    public void ExpandsUserProfileVariable()
    {
        var path = KnownFolders.Resolve(Loaded, "Desktop", (_, _) => @"%USERPROFILE%\Desktop");
        Assert.Equal(@"C:\Users\ivanov.CORP\Desktop", path);
    }

    [Fact]
    public void FallsBackToDefaultWhenUserLoggedOff()
    {
        var offline = Loaded with { HiveLoaded = false };
        var path = KnownFolders.Resolve(offline, "Downloads", (_, _) => throw new InvalidOperationException("не должен читаться"));
        Assert.Equal(@"C:\Users\ivanov.CORP\Downloads", path);
    }

    [Fact]
    public void UnknownFolderIsRelativePath() =>
        Assert.Equal(@"C:\Users\ivanov.CORP\AppData\Roaming\App",
            KnownFolders.Resolve(Loaded, @"AppData\Roaming\App", (_, _) => null));
}

public sealed class ResolvePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mybackup-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SkipsMissingAndNetworkFolders()
    {
        var ivanov = Path.Combine(_root, "ivanov");
        var petrov = Path.Combine(_root, "petrov");
        Directory.CreateDirectory(Path.Combine(ivanov, "Desktop"));
        Directory.CreateDirectory(Path.Combine(ivanov, "Documents"));
        Directory.CreateDirectory(petrov); // пустой профиль без папок

        UserProfile[] profiles =
        [
            new("S-1-5-21-1", "ivanov", ivanov, true),
            new("S-1-5-21-2", "petrov", petrov, false),
        ];
        var config = new UserProfilesSourceConfig { Folders = ["Desktop", "Documents", "Downloads"] };

        var (paths, notes, users) = UserProfilesSource.ResolvePaths(config, profiles,
            (sid, value) => sid == "S-1-5-21-1" && value == "Personal" ? @"\\fs\home\ivanov\Documents" : null);

        Assert.Equal([Path.Combine(ivanov, "Desktop")], paths);
        Assert.Equal(1, users);
        Assert.Contains(notes, n => n.Contains("сетевом ресурсе"));
    }
}
