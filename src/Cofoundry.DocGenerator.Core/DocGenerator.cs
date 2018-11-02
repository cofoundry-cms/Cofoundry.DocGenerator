using Cofoundry.Core;
using Cofoundry.Core.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Cofoundry.DocGenerator.Core
{
    public class DocGenerator
    {
        private const string MARKDOWN_FILE_EXTENSION = ".md";
        private const string STATIC_FOLDER_NAME = "static";

        private static HttpClient _httpClient = new HttpClient();

        private readonly IDestinationFileStoreService _fileWriterService;
        private readonly DocGeneratorSettings _docGeneratorSettings;

        public DocGenerator(DocGeneratorSettings docGeneratorSettings)
        {
            _docGeneratorSettings = docGeneratorSettings;

            if (docGeneratorSettings.UseAzure)
            {
                _fileWriterService = new AzureBlobDestinationFileStoreService(_docGeneratorSettings);
            }
            else
            {
                _fileWriterService = new FileSystemDestinationFileStoreService(_docGeneratorSettings.OutputPath);
            }
        }

        public async Task GenerateAsync()
        {
            var version = _docGeneratorSettings.Version;
            var sourcePath = _docGeneratorSettings.SourcePath;

            var staticFileFolder = FilePathHelper.CombineVirtualPath(STATIC_FOLDER_NAME, version);

            await _fileWriterService.EnsureDirectoryExistsAsync(version);
            await _fileWriterService.EnsureDirectoryExistsAsync(staticFileFolder);

            if (_docGeneratorSettings.CleanDestination)
            {
                await _fileWriterService.ClearDirectoryAsync(version);
                await _fileWriterService.ClearDirectoryAsync(staticFileFolder);
            }

            var rootNode = new DocumentationNode()
            {
                Title = "Docs",
                Url = "/" + version,
                UpdateDate = DateTime.UtcNow
            };

            // Copy files/directories recursively
            await ProcessDirectory(sourcePath, rootNode);

            // Write the completed table of contents file
            var serialized = JsonConvert.SerializeObject(rootNode, GetJsonSetting());
            await _fileWriterService.WriteText(serialized, FilePathHelper.CombineVirtualPath(rootNode.Url, "toc.json"));

            // update the version manifest file in the root
            await UpdateVersionsAsync();

            await PublishWebHook();
        }

        private async Task PublishWebHook()
        {
            if (string.IsNullOrEmpty(_docGeneratorSettings.OnCompleteWebHook)) return;

            await _httpClient.PostAsync(_docGeneratorSettings.OnCompleteWebHook, null);
        }

        private async Task UpdateVersionsAsync()
        {
            var rootDirectoryFiles = await _fileWriterService.GetDirectoryNamesAsync("/");
            var versions = rootDirectoryFiles
                .Select(d => StringHelper.SplitAndTrim(d, Path.DirectorySeparatorChar).LastOrDefault())
                .Where(d => Regex.IsMatch(d, "^\\d+\\.\\d+\\.\\d+$"))
                .OrderByDescending(v => v)
                .ToList();

            var serialized = JsonConvert.SerializeObject(versions, GetJsonSetting());
            await _fileWriterService.WriteText(serialized, "/versions.json");
        }

        private async Task ProcessDirectory(string directoryPath, DocumentationNode parentNode)
        {
            var allFilePaths = Directory.GetFiles(directoryPath);
            if (allFilePaths.Length == 0) return;

            var resultNodes = new Dictionary<string, DocumentationNode>();

            // Read the redirects.json file if exists and map the contents
            var redirects = await ProcessRedirects(resultNodes, parentNode, allFilePaths);

            // if a directory level redirect was assigned, return.
            if (parentNode.RedirectTo != null) return;

            await ProcessFiles(resultNodes, parentNode, allFilePaths, redirects);
            await ProcessChildDirectories(resultNodes, parentNode, directoryPath);

            // Set ordering, set root file
            string[] tableOfContents = null;
            var tableOfContentsFile = allFilePaths.SingleOrDefault(f => Path.GetFileName(f) == "toc.json");

            if (tableOfContentsFile != null)
            {
                tableOfContents = await DeserializeJsonFile<string[]>(tableOfContentsFile);
            }

            // If we have a toc file use it to filter out unwanted items and set ordering
            // otherwise order by title
            if (tableOfContents != null)
            {
                parentNode.Children = resultNodes
                    .FilterAndOrderByKeys(tableOfContents.Select(SlugFormatter.ToSlug))
                    .ToList();
            }
            else
            {
                parentNode.Children = resultNodes
                    .Select(r => r.Value)
                    .OrderBy(r => r.Title)
                    .ToList();
            }
        }

        private static async Task<Dictionary<string, string>> ProcessRedirects(
            Dictionary<string, DocumentationNode> resultNodes,
            DocumentationNode parentNode,
            string[] allFilePaths
            )
        {
            Dictionary<string, string> redirects = null;
            var redirectsFile = allFilePaths.SingleOrDefault(f => Path.GetFileName(f) == "redirects.json");
            DateTime updateDate;

            if (!string.IsNullOrEmpty(redirectsFile))
            {
                redirects = await DeserializeJsonFile<Dictionary<string, string>>(redirectsFile);
                updateDate = File.GetLastWriteTimeUtc(redirectsFile);
            }
            else
            {
                redirects = new Dictionary<string, string>();
                return redirects;
            }

            var directoryRedirectRule = redirects.GetOrDefault("*");
            if (directoryRedirectRule != null)
            {
                // If we have a directory level redirect rule, assign it to the
                // the directory node and return
                parentNode.RedirectTo = directoryRedirectRule;
                parentNode.UpdateDate = updateDate;

                return redirects;
            }

            foreach (var redirect in redirects)
            {
                var slug = SlugFormatter.ToSlug(redirect.Key);
                var path = FilePathHelper.CombineVirtualPath(parentNode.Url, slug);
                var node = new DocumentationNode()
                {
                    Title = redirect.Key,
                    Url = slug,
                    RedirectTo = redirect.Value,
                    UpdateDate= updateDate
                };

                resultNodes.Add(slug, node);
            }

            return redirects ?? new Dictionary<string, string>();
        }

        private async Task ProcessFiles(
            Dictionary<string, DocumentationNode> resultNodes,
            DocumentationNode parentNode,
            string[] allFilePaths,
            Dictionary<string, string> redirects)
        {
            foreach (var filePath in allFilePaths)
            {
                // slug path
                var fileExtension = Path.GetExtension(filePath);
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                var sluggedFileName = SlugFormatter.ToSlug(fileNameWithoutExtension);
                var sluggedFileNameWithExtension = Path.ChangeExtension(sluggedFileName, fileExtension);

                // skip the file if there's already a redirect
                if (redirects.Any(r => SlugFormatter.ToSlug(r.Key) == sluggedFileName)) continue;

                if (fileExtension == MARKDOWN_FILE_EXTENSION)
                {
                    var destinationFilePath = FilePathHelper.CombineVirtualPath(parentNode.Url, sluggedFileNameWithExtension);
                    var updateDate = File.GetLastWriteTimeUtc(filePath);

                    // if we have a custom index file, map this to the container directory
                    if (sluggedFileName == "index")
                    {
                        parentNode.DocumentFilePath = destinationFilePath;
                        parentNode.UpdateDate = updateDate;
                    }
                    else
                    {
                        var node = new DocumentationNode()
                        {
                            Title = fileNameWithoutExtension,
                            Url = FilePathHelper.CombineVirtualPath(parentNode.Url, sluggedFileName),
                            DocumentFilePath = destinationFilePath,
                            UpdateDate = updateDate
                        };

                        resultNodes.Add(sluggedFileName, node);
                    }

                    await _fileWriterService.CopyFile(filePath, destinationFilePath);
                }
                else if (fileExtension != ".json")
                {
                    // static files are served out of a separate directory
                    var destinationDirectory = FilePathHelper.CombineVirtualPath(STATIC_FOLDER_NAME, parentNode.Url);
                    var destinationPath = FilePathHelper.CombineVirtualPath(destinationDirectory, sluggedFileNameWithExtension);

                    await _fileWriterService.EnsureDirectoryExistsAsync(destinationDirectory);
                    await _fileWriterService.CopyFile(filePath, destinationPath);
                }

                // json files ignore, they are assumed to be config
            }
        }

        private async Task ProcessChildDirectories(Dictionary<string, DocumentationNode> resultNodes, DocumentationNode parentNode, string directoryPath)
        {
            foreach (var childDirectoryPath in Directory.GetDirectories(directoryPath))
            {
                var directory = new DirectoryInfo(childDirectoryPath);
                var slug = SlugFormatter.ToSlug(directory.Name);

                var childNode = new DocumentationNode()
                {
                    Title = directory.Name,
                    Url = FilePathHelper.CombineVirtualPath(parentNode.Url, slug)
                };

                childNode.UpdateDate = Directory.GetLastWriteTimeUtc(childDirectoryPath);

                await _fileWriterService.EnsureDirectoryExistsAsync(childNode.Url);
                await ProcessDirectory(childDirectoryPath, childNode);

                // Only add the node if it represents something in the document tree
                // i.e. skip static resource directories.
                if (childNode.Children.Any() 
                    || childNode.DocumentFilePath != null
                    || childNode.RedirectTo != null)
                {
                    resultNodes.Add(slug, childNode);
                }
            }
        }

        private static async Task<T> DeserializeJsonFile<T>(string redirectsFile)
        {
            var text = await File.ReadAllTextAsync(redirectsFile);
            var redirects = JsonConvert.DeserializeObject<T>(text);
            return redirects;
        }

        private JsonSerializerSettings GetJsonSetting()
        {
            return new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy()
                }
            };
        }
    }
}
