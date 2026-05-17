using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Extensions.ValueTask;
using Soenneker.Git.Util.Abstract;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using Soenneker.ProductBoard.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.Yaml.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.ProductBoard.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private const string _bundledSpecBaseUrl = "https://developer.productboard.com/openapi/";

    private static readonly List<string> _bundledSpecFileNames =
    [
        "analytics.yaml",
        "entities.yaml",
        "jira-integrations.yaml",
        "members.yaml",
        "notes.yaml",
        "plugin-integrations.yaml",
        "teams.yaml",
        "webhooks.yaml"
    ];

    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IOpenApiMerger _openApiMerger;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IYamlUtil _yamlUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil, IOpenApiMerger openApiMerger,
        IOpenApiFixer openApiFixer, IFileDownloadUtil fileDownloadUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IYamlUtil yamlUtil,
        IKiotaUtil kiotaUtil)
    {
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _openApiMerger = openApiMerger;
        _openApiFixer = openApiFixer;
        _kiotaUtil = kiotaUtil;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _yamlUtil = yamlUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string legacyYamlPath = Path.Combine(gitDirectory, "openapi.yaml");
        await _fileUtil.DeleteIfExists(legacyYamlPath, cancellationToken: cancellationToken);

        string yamlDirectory = Path.Combine(gitDirectory, "openapi-yaml");
        string jsonDirectory = Path.Combine(gitDirectory, "openapi-json");
        await _directoryUtil.Create(yamlDirectory, cancellationToken: cancellationToken);
        await _directoryUtil.Create(jsonDirectory, cancellationToken: cancellationToken);

        foreach (string fileName in _bundledSpecFileNames)
        {
            string yamlPath = Path.Combine(yamlDirectory, fileName);
            await _fileUtil.DeleteIfExists(yamlPath, cancellationToken: cancellationToken);

            string url = _bundledSpecBaseUrl + fileName;
            _logger.LogInformation("Downloading ProductBoard OpenAPI bundle {FileName} ...", fileName);
            string? yamlFilePath = await _fileDownloadUtil.Download(url, yamlPath, fileExtension: Path.GetExtension(fileName), cancellationToken: cancellationToken);

            string jsonTargetPath = Path.Combine(jsonDirectory, Path.ChangeExtension(fileName, ".json"));
            await _fileUtil.DeleteIfExists(jsonTargetPath, cancellationToken: cancellationToken);

            await _yamlUtil.SaveAsJson(yamlFilePath ?? yamlPath, jsonTargetPath, true, cancellationToken);
        }

        string mergedJson = _openApiMerger.ToJson(await _openApiMerger.MergeDirectory(jsonDirectory, cancellationToken).NoSync());

        string mergedJsonPath = Path.Combine(jsonDirectory, "merged-openapi.json");
        await _fileUtil.DeleteIfExists(mergedJsonPath, cancellationToken: cancellationToken);
        await _fileUtil.Write(mergedJsonPath, mergedJson, cancellationToken: cancellationToken);

        string fixedFilePath = Path.Combine(jsonDirectory, "fixed.json");
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);
        await _openApiFixer.Fix(mergedJsonPath, fixedFilePath, cancellationToken).NoSync();

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "ProductBoardOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await _fileUtil.Delete(mergedJsonPath, cancellationToken: cancellationToken);
        await _fileUtil.Delete(fixedFilePath, cancellationToken: cancellationToken);

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
