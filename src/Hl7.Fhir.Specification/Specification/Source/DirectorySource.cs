﻿/* 
 * Copyright (c) 2017, Furore (info@furore.com) and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the BSD 3-Clause license
 * available at https://github.com/ewoutkramer/fhir-net-api/blob/master/LICENSE
 */

// [WMR 20171010] Use new ArtifactSummary instead of obsolete ConformanceScanInformation
#define ARTIFACTSUMMARY

using System;
using System.Collections.Generic;
using System.Linq;
using Hl7.Fhir.Model;
using Hl7.Fhir.Support;
using System.IO;
using System.Xml;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Hl7.Fhir.Serialization;
using Newtonsoft.Json;
using System.Diagnostics;

namespace Hl7.Fhir.Specification.Source
{
#if NET_FILESYSTEM
    /// <summary>
    /// Reads FHIR artifacts (Profiles, ValueSets, ...) from directories with individual files
    /// </summary>
    public class DirectorySource : IConformanceSource, IArtifactSource
    {
        private readonly string _contentDirectory;
        private readonly bool _includeSubs;

        private string[] _masks;
        private string[] _includes;
        private string[] _excludes;

        /// <summary>
        /// Gets or sets the search string to match against the names of files in the content directory.
        /// The source will only provide resources from files that match the specified mask.
        /// The source will ignore all files that don't match the specified mask.
        /// Multiple masks can be split by '|'
        /// </summary>
        public string Mask
        {
            get => String.Join("|", Masks);
            set
            {
                Masks = value?.Split('|').Select(s => s.Trim()).Where(s => !String.IsNullOrEmpty(s)).ToArray();
            }
        }

        public string[] Masks
        {
            get { return _masks; }
            set { _masks = value; Refresh(); }
        }

        public string[] Includes
        {
            get { return _includes; }
            set { _includes = value; Refresh(); }
        }

        public string[] Excludes
        {
            get { return _excludes; }
            set { _excludes = value; Refresh(); }
        }


        public DirectorySource(string contentDirectory, bool includeSubdirectories = false)
        {
            _contentDirectory = contentDirectory ?? throw Error.ArgumentNull(nameof(contentDirectory));
            _includeSubs = includeSubdirectories;
        }


        public DirectorySource(bool includeSubdirectories = false) : this(SpecificationDirectory, includeSubdirectories)
        {
        }


        /// <summary>
        /// The default directory this artifact source will access for its files.
        /// </summary>
        public static string SpecificationDirectory
        {
            get
            {
#if DOTNETFW
                var codebase = AppDomain.CurrentDomain.BaseDirectory;
#else
                var codebase = AppContext.BaseDirectory;
#endif
                if (Directory.Exists(codebase))
                    return codebase;
                else
                    return Directory.GetCurrentDirectory();
            }
        }

        /// <summary>Returns the content directory as specified to the constructor.</summary>
        public string ContentDirectory => _contentDirectory;

        private bool _filesPrepared = false;
        private List<string> _artifactFilePaths;

        /// <summary>
        /// Prepares the source by reading all files present in the directory (matching the mask, if given)
        /// </summary>
        private void prepareFiles()
        {
            if (_filesPrepared) return;

            var masks = _masks ?? (new[] { "*.*" });

            // Add files present in the content directory
            var allFiles = new List<string>();

            // [WMR 20170817] NEW
            // Safely enumerate files in specified path and subfolders, recursively
            allFiles.AddRange(SafeGetFiles(_contentDirectory, masks, _includeSubs));
            //foreach (var mask in masks)
            //{
            //    allFiles.AddRange(Directory.GetFiles(_contentDirectory, mask, _includeSubs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
            //}

            // Always remove *.exe" and "*.dll"
            allFiles.RemoveAll(name => Path.GetExtension(name) == ".exe" || Path.GetExtension(name) == ".dll");

            if (_includes?.Length > 0)
            {
                var includeFilter = new FilePatternFilter(_includes);
                allFiles = includeFilter.Filter(_contentDirectory, allFiles).ToList();
            }

            if (_excludes?.Length > 0)
            {
                var excludeFilter = new FilePatternFilter(_excludes, negate: true);
                allFiles = excludeFilter.Filter(_contentDirectory, allFiles).ToList();
            }

            _artifactFilePaths = allFiles;
            _filesPrepared = true;
        }

        // [WMR 20170817]
        // Safely enumerate files in specified path and subfolders, recursively
        // Ignore files & folders with Hidden and/or System attributes
        // Ignore subfolders with insufficient access permissions
        // https://stackoverflow.com/a/38959208
        // https://docs.microsoft.com/en-us/dotnet/standard/io/how-to-enumerate-directories-and-files

        private static IEnumerable<string> SafeGetFiles(string path, IEnumerable<string> masks, bool searchSubfolders)
        {
            if (File.Exists(path))
            {
                return new string[] { path };
            }

            if (!Directory.Exists(path))
            {
                return Enumerable.Empty<string>();
            }

            // Not necessary; caller prepareFiles() validates the mask
            //if (!masks.Any())
            //{
            //    return Enumerable.Empty<string>();
            //}

            Queue<string> folders = new Queue<string>();
            // Use HashSet to remove duplicates; different masks could match same file(s)
            HashSet<string> files = new HashSet<string>();
            folders.Enqueue(path);

            while (folders.Count != 0)
            {
                string currentFolder = folders.Dequeue();
                var currentDirInfo = new DirectoryInfo(currentFolder);

                // local helper function to validate file/folder attributes, exclude system and/or hidden
                bool IsValid(FileAttributes attr) => (attr & (FileAttributes.System | FileAttributes.Hidden)) == 0;

                foreach (var mask in masks)
                {
                    try
                    {
                        // https://docs.microsoft.com/en-us/dotnet/standard/io/how-to-enumerate-directories-and-files
                        // "Although you can immediately enumerate all the files in the subdirectories of a
                        // parent directory by using the AllDirectories search option provided by the SearchOption
                        // enumeration, unauthorized access exceptions (UnauthorizedAccessException) may cause the
                        // enumeration to be incomplete. If these exceptions are possible, you can catch them and
                        // continue by first enumerating directories and then enumerating files."

                        // Explicitly ignore system & hidden files
                        var curFiles = currentDirInfo.EnumerateFiles(mask, SearchOption.TopDirectoryOnly);
                        foreach (var file in curFiles)
                        {
                            // Skip system & hidden files
                            if (IsValid(file.Attributes))
                            {
                                files.Add(file.FullName);
                            }
                        }
                    }
#if DEBUG
                    catch (Exception ex)
                    {
                        // Do Nothing
                        Debug.WriteLine($"Error enumerating files in '{currentFolder}': {ex.Message}");
                    }
#else
                    catch { }
#endif
                }

                if (searchSubfolders)
                {
                    try
                    {
                        var subFolders = currentDirInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly);
                        foreach (var subFolder in subFolders)
                        {
                            // Skip system & hidden folders
                            if (IsValid(subFolder.Attributes))
                            {
                                folders.Enqueue(subFolder.FullName);
                            }
                        }
                    }
#if DEBUG
                    catch (Exception ex)
                    {
                        // Do Nothing
                        Debug.WriteLine($"Error enumerating subfolders of '{currentFolder}': {ex.Message}");
                    }
#else
                    catch { }
#endif

                }
            }

            return files.AsEnumerable();
        }


        internal static List<string> ResolveDuplicateFilenames(List<string> allFilenames, DuplicateFilenameResolution preference)
        {
            var result = new List<string>();
            var xmlOrJson = new List<string>();

            foreach (var filename in allFilenames.Distinct())
            {
                if (isXml(filename) || isJson(filename))
                    xmlOrJson.Add(filename);
                else
                    result.Add(filename);
            }

            var groups = xmlOrJson.GroupBy(path => fullPathWithoutExtension(path));
            
            foreach (var group in groups)
            {
                if (group.Count() == 1 || preference == DuplicateFilenameResolution.KeepBoth)
                    result.AddRange(group);
                else
                {
                    // count must be 2
                    var first = group.First();
                    if (preference == DuplicateFilenameResolution.PreferXml && isXml(first))
                        result.Add(first);
                    else if (preference == DuplicateFilenameResolution.PreferJson && isJson(first))
                        result.Add(first);
                    else
                        result.Add(group.Skip(1).First());                
                }
            }

            return result;

            string fullPathWithoutExtension(string fullPath) => fullPath.Replace(Path.GetFileName(fullPath), Path.GetFileNameWithoutExtension(fullPath));
            bool isXml(string fullPath) => Path.GetExtension(fullPath).ToLower() == ".xml";
            bool isJson(string fullPath) => Path.GetExtension(fullPath).ToLower() == ".json";
        }

        bool _resourcesPrepared = false;
#if ARTIFACTSUMMARY
        private List<ArtifactSummary> _resourceScanInformation;
#else
        private List<ConformanceScanInformation> _resourceScanInformation;
#endif

        public enum DuplicateFilenameResolution
        {
            PreferXml,
            PreferJson,
            KeepBoth
        }


        public DuplicateFilenameResolution FormatPreference
        {
            get;
            set;
        } = DuplicateFilenameResolution.PreferXml;


        // [WMR 20170217] Ignore invalid xml files, aggregate parsing errors
        // https://github.com/ewoutkramer/fhir-net-api/issues/301
        public struct ErrorInfo
        {
            public ErrorInfo(string fileName, Exception error) { FileName = fileName; Error = error; }
            public string FileName { get; }
            public Exception Error { get; }
        }

        /// <summary>Returns an array of runtime errors that occured while parsing the resources.</summary>
        public ErrorInfo[] Errors = new ErrorInfo[0];

        /// <summary>Scan all xml files found by prepareFiles and find conformance resources and their id.</summary>
        private void prepareResources()
        {
            if (_resourcesPrepared) return;
            prepareFiles();

            var uniqueArtifacts = ResolveDuplicateFilenames(_artifactFilePaths, FormatPreference);
            (_resourceScanInformation, Errors) = scanPaths(uniqueArtifacts);

            // Check for duplicate canonical urls, this is forbidden within a single source (and actually, universally,
            // but if another source has the same url, the order of polling in the MultiArtifactSource matters)
            var doubles = from ci in _resourceScanInformation
#if ARTIFACTSUMMARY
                          .OfType<ConformanceResourceSummary>()
#endif
                          where ci.Canonical != null
                          group ci by ci.Canonical into g
                          where g.Count() > 1
                          select g;

            if (doubles.Any())
                throw new CanonicalUrlConflictException(doubles.Select(d => new CanonicalUrlConflictException.CanonicalUrlConflict(d.Key, d.Select(ci => ci.Origin))));

            _resourcesPrepared = true;
            return;

#if ARTIFACTSUMMARY
            (List<ArtifactSummary>, ErrorInfo[]) scanPaths(List<string> paths)
            {
                var scanResult = new List<ArtifactSummary>();
#else
            (List<ConformanceScanInformation>, ErrorInfo[]) scanPaths(List<string> paths)
            {
                var scanResult = new List<ConformanceScanInformation>();
#endif

                var errors = new List<ErrorInfo>();

                foreach (var filePath in paths)
                {
                    try
                    {
                        var navStream = createNavigatorStream(filePath);
                        // createNavigatorStream returns null for unknown file extensions
                        if (navStream != null)
                        {
                            var harvester = ArtifactSummaryHarvester.Default;
                            var summaryStream = harvester.Enumerate(navStream);
                            scanResult.AddRange(summaryStream);
                        }
                    }
                    catch (XmlException ex)
                    {
                        errors.Add(new ErrorInfo(filePath, ex));    // Log the exception
                    }
                    catch(JsonException ej)
                    {
                        errors.Add(new ErrorInfo(filePath, ej));    // Log the exception
                    }
                    // Don't catch other exceptions (fatal error)
                }

                return (scanResult, errors.ToArray());

            }
        }

#if ARTIFACTSUMMARY
        // TODO: Instead, return INavigatorStream
        // 1. DirectorySource creates streamer
        // 2. DirectorySource initializes harvester with streamer
        // 3. Harvester generates summaries

        // [WMR 20171016] Allow subclasses to override this method
        // and return custom INavigatorStream implementations
        protected virtual INavigatorStream createNavigatorStream(string path)
        {
            var ext = Path.GetExtension(path);

            if (StringComparer.OrdinalIgnoreCase.Equals(ext, ".xml"))
            {
                return new XmlNavigatorStream(path);
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(ext, ".json"))
            {
                return new JsonNavigatorStream(path);
            }

            // Unsupported extension
            return null;
        }
#else
        private static IConformanceScanner createScanner(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            return ext == ".xml" ? new XmlFileConformanceScanner(path) :
                          ext == ".json" ? new JsonFileConformanceScanner(path) : (IConformanceScanner)null;
        }
#endif



        public void Refresh()
        {
            _filesPrepared = false;
            _resourcesPrepared = false;
        }

        public IEnumerable<string> ListArtifactNames()
        {
            prepareFiles();

            return _artifactFilePaths.Select(path => Path.GetFileName(path));
        }

        public Stream LoadArtifactByName(string name)
        {
            if (name == null) throw Error.ArgumentNull(nameof(name));

            prepareFiles();

            var searchString = (Path.DirectorySeparatorChar + name).ToLower();

            // NB: uses _artifactFiles (full paths), not ArtifactFiles (which only has public list of names, not full path)
            var fullFileName = _artifactFilePaths.SingleOrDefault(fn => fn.ToLower().EndsWith(searchString));

            return fullFileName == null ? null : File.OpenRead(fullFileName);
        }


        public IEnumerable<string> ListResourceUris(ResourceType? filter = null)
        {
            prepareResources();

#if ARTIFACTSUMMARY
            IEnumerable<ArtifactSummary> scan = _resourceScanInformation;
#else
            IEnumerable<ConformanceScanInformation> scan = _resourceScanInformation;
#endif
            if (filter != null)
            {
                scan = scan.Where(dsi => dsi.ResourceType == filter);
            }

            return scan.Select(dsi => dsi.ResourceUri);
        }


        public Resource ResolveByUri(string uri)
        {
            if (uri == null) throw Error.ArgumentNull(nameof(uri));
            prepareResources();

            var info = _resourceScanInformation.SingleOrDefault(ci => ci.ResourceUri == uri);
            if (info == null) return null;

            return getResourceFromScannedSource(info);

        }

        public Resource ResolveByCanonicalUri(string uri)
        {
            if (uri == null) throw Error.ArgumentNull(nameof(uri));
            prepareResources();

            var info = _resourceScanInformation
#if ARTIFACTSUMMARY
                .OfType<ConformanceResourceSummary>()
#endif
                .SingleOrDefault(ci => ci.Canonical == uri);
                         

            if (info == null) return null;

            return getResourceFromScannedSource(info);
        }

#if ARTIFACTSUMMARY
        private static Resource getResourceFromScannedSource(ArtifactSummary info)
#else
        private static Resource getResourceFromScannedSource(ConformanceScanInformation info)
#endif
        {
            // [WMR 20171016] TODO: rewrite obsolete logic

            // var path = info.Origin;
            // var scanner = createScanner(path);
            // return scanner.Retrieve(info);

            throw new NotImplementedException("TODO: Call new overload on deserializers that accept an IElementNavigator.");
        }

        public ValueSet FindValueSetBySystem(string system)
        {
            prepareResources();

            var info = _resourceScanInformation
#if ARTIFACTSUMMARY
                .OfType<ValueSetSummary>()
#endif
                .SingleOrDefault(ci => ci.ValueSetSystem == system);

            if (info == null) return null;

            return getResourceFromScannedSource(info) as ValueSet;
        }

        public IEnumerable<ConceptMap> FindConceptMaps(string sourceUri = null, string targetUri = null)
        {
            if (sourceUri == null && targetUri == null)
            {
                throw Error.ArgumentNull(nameof(targetUri), "sourceUri and targetUri cannot both be null");
            }

            prepareResources();

#if ARTIFACTSUMMARY
            IEnumerable<ConceptMapSummary> infoList = _resourceScanInformation.OfType<ConceptMapSummary>();
#else
            IEnumerable<ConformanceScanInformation> infoList = _resourceScanInformation;
#endif
            if (sourceUri != null)
            {
                infoList = infoList.Where(ci => ci.ConceptMapSource == sourceUri);
            }

            if (targetUri != null)
            {
                infoList = infoList.Where(ci => ci.ConceptMapTarget == targetUri);
            }

            return infoList.Select(info => getResourceFromScannedSource(info)).Where(r => r != null).Cast<ConceptMap>();
        }

        public NamingSystem FindNamingSystem(string uniqueId)
        {
            if (uniqueId == null) throw Error.ArgumentNull(nameof(uniqueId));
            prepareResources();

            var info = _resourceScanInformation
#if ARTIFACTSUMMARY
                .OfType<NamingSystemSummary>()
#endif
                .SingleOrDefault(ci => ci.UniqueIds.Contains(uniqueId));

            if (info == null) return null;

            return getResourceFromScannedSource(info) as NamingSystem;
        }
    }
#endif

}
