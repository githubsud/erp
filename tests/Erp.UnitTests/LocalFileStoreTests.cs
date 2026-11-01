using Erp.Infrastructure.Files;

namespace Erp.UnitTests;

public sealed class LocalFileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "erp-files-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Saves_opens_and_deletes_a_file_under_its_key()
    {
        var store = new LocalFileStore(root);
        await store.SaveAsync("t1/commerce/a1.jpg", new MemoryStream([1, 2, 3]), default);
        File.Exists(Path.Combine(root, "t1", "commerce", "a1.jpg")).ShouldBeTrue();
        await using (var read = await store.OpenAsync("t1/commerce/a1.jpg", default))
        {
            var copy = new MemoryStream();
            await read!.CopyToAsync(copy);
            copy.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        }
        Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).ShouldBeEmpty();
        await store.DeleteAsync("t1/commerce/a1.jpg", default);
        (await store.OpenAsync("t1/commerce/a1.jpg", default)).ShouldBeNull();
        await store.DeleteAsync("t1/commerce/a1.jpg", default);
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("t1/../../outside.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("t1\a.jpg")]
    [InlineData("T1/A.JPG")]
    [InlineData("t1//a.jpg")]
    [InlineData("t1/a..jpg")]
    [InlineData("")]
    public async Task Refuses_keys_that_could_leave_the_folder(string key)
    {
        var store = new LocalFileStore(root);
        await Should.ThrowAsync<ArgumentException>(() => store.SaveAsync(key, new MemoryStream([1]), default));
        await Should.ThrowAsync<ArgumentException>(() => store.OpenAsync(key, default));
    }

    [Fact]
    public void Files_default_to_beside_the_program_folder_never_inside_it()
    {
        var program = Path.Combine(root, "app");
        new StorageOptions().ResolveRoot(program).ShouldBe(Path.Combine(root, "files"));
        new StorageOptions { FilesRoot = "App_Data/files" }.ResolveRoot(program).ShouldBe(Path.GetFullPath(Path.Combine(program, "App_Data", "files")));
        new StorageOptions { FilesRoot = @"D:\shop-files" }.ResolveRoot(program).ShouldBe(@"D:\shop-files");
    }
}
