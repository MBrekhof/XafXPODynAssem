namespace XafXPODynAssem.Module.Services
{
    public interface ISchemaFileService
    {
        Task DownloadJsonAsync(string fileName, string jsonContent);
        Task<string> UploadJsonAsync();
    }
}
