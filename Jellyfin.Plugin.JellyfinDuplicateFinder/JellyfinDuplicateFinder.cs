using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyfinDuplicateFinder.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyfinDuplicateFinder;

/// <summary>
/// The main plugin.
/// </summary>
public class JellyfinDuplicateFinder : BasePlugin<JellyfinDuplicateFinderConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinDuplicateFinder"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public JellyfinDuplicateFinder(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "JellyfinDuplicateFinder";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("90d2d15b-0c16-4d56-90d7-f4b63a9b1f1b");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static JellyfinDuplicateFinder? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var assembly = GetType().Assembly;

        string GetResourceName(string pageName, string extension)
        {
#pragma warning disable CA2201 // Do not raise reserved exception types
            return assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith($"{pageName}.{extension}", true, CultureInfo.InvariantCulture))
            ?? string.Empty;
#pragma warning restore CA2201 // Do not raise reserved exception types
        }

        yield return new PluginPageInfo
        {
            Name = Name,
            // EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.JellyfinDuplicateFinder.html", GetType().Namespace)
            EmbeddedResourcePath = GetResourceName("JellyfinDuplicateFinder", "html")
        };
    }
}
