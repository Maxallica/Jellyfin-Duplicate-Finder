using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinDuplicateFinder.Controller
{
    /// <summary>
    /// Controller for the Jellyfin Duplicate Finder plugin.
    /// Provides API endpoints for testing and deleting duplicate movies.
    /// </summary>
    [Route("Plugins/JellyfinDuplicateFinder")]
    [ApiController]
    public class JellyfinDuplicateFinderController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<JellyfinDuplicateFinderController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="JellyfinDuplicateFinderController"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager service instance.</param>
        /// <param name="logger">Logger instance for diagnostic messages.</param>
        public JellyfinDuplicateFinderController(
            ILibraryManager libraryManager,
            ILogger<JellyfinDuplicateFinderController> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// A simple test endpoint to verify the controller is reachable.
        /// </summary>
        /// <returns>Plain text response "ok".</returns>
        [HttpGet("test")]
        public ActionResult<string> TestEndpoint()
        {
            return new ContentResult
            {
                Content = "ok",
                ContentType = "text/plain; charset=utf-8",
                StatusCode = 200
            };
        }

        /// <summary>
        /// Endpoint to trigger duplicate movie detection and (optionally) deletion.
        /// </summary>
        /// <param name="test">
        /// Query parameter: if "true", runs in simulation mode (no files deleted);
        /// if "false", deletes duplicate movie files.
        /// </param>
        /// <returns>Plain text report of operations performed.</returns>
        [HttpGet("delete-duplicates")]
        public async Task<IActionResult> TriggerDeleteDuplicateMovies([FromQuery] string? test = "true")
        {
            var isTestMode = !string.Equals(test, "false", StringComparison.OrdinalIgnoreCase);
            var result = await DeleteDuplicateMoviesAsync(isTestMode).ConfigureAwait(false);

            if (!isTestMode)
            {
                try
                {
                    _libraryManager.QueueLibraryScan();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "QueueLibraryScan not available or failed.");
                }
            }

            return new ContentResult
            {
                Content = result,
                ContentType = "text/plain; charset=utf-8",
                StatusCode = 200
            };
        }

        /// <summary>
        /// Scans the Jellyfin library for duplicate movies (by IMDB ID) and deletes lower quality copies.
        /// Uses reflection to access Media and Streams properties since they are not directly available in the current DLL.
        /// </summary>
        /// <param name="test">If true, no files are deleted (simulation mode).</param>
        /// <returns>Plain text report of actions taken or simulated.</returns>
        private async Task<string> DeleteDuplicateMoviesAsync(bool test)
        {
            var sb = new System.Text.StringBuilder();

            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive = true,
                    DtoOptions = new DtoOptions(true)
                };

                var movies = _libraryManager.GetItemsResult(query).Items;

                // Group movies by IMDB ID
                var groupedMovies = movies
                    .Where(m => m.ProviderIds != null && m.ProviderIds.ContainsKey("Imdb"))
                    .GroupBy(m => m.ProviderIds["Imdb"]);

                foreach (var group in groupedMovies)
                {
                    // TODO: Replace this reflection-based Media/Streams access with the original BaseItem methods once the updated Jellyfin.Controller DLL is available.
                    // Using reflection is a temporary workaround due to missing Media property in the current DLL.
                    // Sort movies by max Height, then max Bitrate, then file size using Reflection
                    var sortedMovies = group
                        .OrderByDescending(m =>
                        {
                            int maxHeight = 0;

                            var mediaProperty = m.GetType().GetProperty("Media");
                            var mediaList = mediaProperty?.GetValue(m) as IEnumerable<object>;
                            if (mediaList != null)
                            {
                                foreach (var media in mediaList)
                                {
                                    var streamsProp = media.GetType().GetProperty("Streams");
                                    var streams = streamsProp?.GetValue(media) as IEnumerable<object>;
                                    if (streams != null)
                                    {
                                        foreach (var stream in streams)
                                        {
                                            var heightProp = stream.GetType().GetProperty("Height");
                                            if (heightProp?.GetValue(stream) is int h)
                                            {
                                                maxHeight = Math.Max(maxHeight, h);
                                            }
                                        }
                                    }
                                }
                            }

                            return maxHeight;
                        })
                        .ThenByDescending(m =>
                        {
                            int maxBitrate = 0;

                            var mediaProperty = m.GetType().GetProperty("Media");
                            var mediaList = mediaProperty?.GetValue(m) as IEnumerable<object>;
                            if (mediaList != null)
                            {
                                foreach (var media in mediaList)
                                {
                                    var streamsProp = media.GetType().GetProperty("Streams");
                                    var streams = streamsProp?.GetValue(media) as IEnumerable<object>;
                                    if (streams != null)
                                    {
                                        foreach (var stream in streams)
                                        {
                                            var bitrateProp = stream.GetType().GetProperty("Bitrate");
                                            if (bitrateProp?.GetValue(stream) is int b)
                                            {
                                                maxBitrate = Math.Max(maxBitrate, b);
                                            }
                                        }
                                    }
                                }
                            }

                            return maxBitrate;
                        })
                        .ThenByDescending(m =>
                        {
                            try
                            {
                                return new FileInfo(m.Path ?? string.Empty).Length;
                            }
                            catch
                            {
                                return 0L;
                            }
                        })
                        .ToList();

                    // Process duplicates, keeping the first (highest quality) movie
                    foreach (var movie in sortedMovies.Skip(1))
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(movie.Path))
                            {
                                _logger.LogWarning("Movie path is null or empty for item {Id}", movie.Id);
                                continue;
                            }

                            var fullPath = Path.GetFullPath(movie.Path);
                            string msg;

                            if (!test)
                            {
                                if (System.IO.File.Exists(fullPath))
                                {
                                    System.IO.File.Delete(fullPath);
                                    _logger.LogInformation("Deleted file {Path}", fullPath);
                                }
                                else
                                {
                                    _logger.LogWarning("File not found: {Path}", fullPath);
                                }
                            }

                            msg = $"File deleted {fullPath} (IMDB={movie.ProviderIds["Imdb"]})";
                            sb.AppendLine(msg);

                            var dir = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                var folder = new DirectoryInfo(dir);
                                if (folder.Exists)
                                {
                                    var folderSize = GetDirectorySize(folder);
                                    if (folderSize < 20 * 1024 * 1024)
                                    {
                                        if (!test)
                                        {
                                            folder.Delete(true);
                                            _logger.LogInformation("Deleted folder {Folder}", folder.FullName);
                                        }

                                        msg = $"Folder deleted {folder.FullName}.";
                                        sb.AppendLine(msg);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing duplicate movie {Id}", movie.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning library for duplicates");
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return sb.ToString();
        }

        /// <summary>
        /// Calculates the total size (in bytes) of a directory recursively.
        /// </summary>
        /// <param name="folder">The directory to measure.</param>
        /// <returns>Total size of files in bytes.</returns>
        private long GetDirectorySize(DirectoryInfo folder)
        {
            try
            {
                return folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute directory size for {Path}", folder.FullName);
                return 0;
            }
        }
    }
}
