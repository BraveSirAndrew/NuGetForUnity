﻿using Assets.NuGet.Editor;

namespace NugetForUnity
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using Ionic.Zip;
    using UnityEditor;
    using UnityEngine;
    using Debug = UnityEngine.Debug;
    using System.Text.RegularExpressions;

    /// <summary>
    /// A set of helper methods that act as a wrapper around nuget.exe
    /// 
    /// TIP: It's incredibly useful to associate .nupkg files as compressed folder in Windows (View like .zip files).  To do this:
    ///      1) Open a command prompt as admin (Press Windows key. Type "cmd".  Right click on the icon and choose "Run as Administrator"
    ///      2) Enter this command: cmd /c assoc .nupkg=CompressedFolder
    /// </summary>
    [InitializeOnLoad]
    public static class NugetHelper
    {
        /// <summary>
        /// The path to the nuget.config file.
        /// </summary>
        public static readonly string NugetConfigFilePath = Path.Combine(Application.dataPath, "./NuGet.config");

        /// <summary>
        /// The path to the packages.config file.
        /// </summary>
        private static readonly string PackagesConfigFilePath = Path.Combine(Application.dataPath, "./packages.config");

        /// <summary>
        /// The path where to put created (packed) and downloaded (not installed yet) .nupkg files.
        /// </summary>
        public static readonly string PackOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NuGet", "Cache");

        /// <summary>
        /// The amount of time, in milliseconds, before the nuget.exe process times out and is killed.
        /// </summary>
        private const int TimeOut = 60000;

        /// <summary>
        /// The loaded NuGet.config file that holds the settings for NuGet.
        /// </summary>
        public static NugetConfigFile NugetConfigFile { get; private set; }

        /// <summary>
        /// Backing field for the packages.config file.
        /// </summary>
        private static PackagesConfigFile packagesConfigFile;

        /// <summary>
        /// Gets the loaded packages.config file that hold the dependencies for the project.
        /// </summary>
        public static PackagesConfigFile PackagesConfigFile
        {
            get
            {
                if (packagesConfigFile == null)
                    RefreshPackageConfig();

                return packagesConfigFile;
            }
        }

        /// <summary>
        /// The list of <see cref="NugetPackageSource"/>s to use.
        /// </summary>
        private static List<NugetPackageSource> packageSources = new List<NugetPackageSource>();

        /// <summary>
        /// The dictionary of currently installed <see cref="NugetPackage"/>s keyed off of their ID string.
        /// </summary>
        private static Dictionary<string, NugetPackage> installedPackages = new Dictionary<string, NugetPackage>();

        /// <summary>
        /// The dictionary of cached credentials retrieved by credential providers, keyed by feed URI.
        /// </summary>
        private static Dictionary<Uri, CredentialProviderResponse?> cachedCredentialsByFeedUri = new Dictionary<Uri, CredentialProviderResponse?>();

        /// <summary>
        /// The current .NET version being used (2.0 [actually 3.5], 4.6, etc).
        /// </summary>
        internal static ApiCompatibilityLevel DotNetVersion;

        /// <summary>
        /// Static constructor used by Unity to initialize NuGet and restore packages defined in packages.config.
        /// </summary>
        static NugetHelper()
        {
            // if we are entering playmode, don't do anything
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

#if UNITY_5_6_OR_NEWER
            DotNetVersion = PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup);
#else
            DotNetVersion = PlayerSettings.apiCompatibilityLevel;
#endif

            // Load the NuGet.config file
            LoadNugetConfigFile();

            // create the nupkgs directory, if it doesn't exist
            if (!Directory.Exists(PackOutputDirectory))
            {
                Directory.CreateDirectory(PackOutputDirectory);
            }
        }

        /// <summary>
        /// Loads the NuGet.config file.
        /// </summary>
        public static void LoadNugetConfigFile()
        {
            if (File.Exists(NugetConfigFilePath))
            {
                NugetConfigFile = NugetConfigFile.Load(NugetConfigFilePath);
            }
            else
            {
                Debug.LogFormat("No NuGet.config file found. Creating default at {0}", NugetConfigFilePath);

                NugetConfigFile = NugetConfigFile.CreateDefaultFile(NugetConfigFilePath);
                AssetDatabase.Refresh();
            }

            // parse any command line arguments
            //LogVerbose("Command line: {0}", Environment.CommandLine);
            packageSources.Clear();
            bool readingSources = false;
            bool useCommandLineSources = false;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (readingSources)
                {
                    if (arg.StartsWith("-"))
                    {
                        readingSources = false;
                    }
                    else
                    {
                        NugetPackageSource source = new NugetPackageSource("CMD_LINE_SRC_" + packageSources.Count, arg);
                        LogVerbose("Adding command line package source {0} at {1}", "CMD_LINE_SRC_" + packageSources.Count, arg);
                        packageSources.Add(source);
                    }
                }

                if (arg == "-Source")
                {
                    // if the source is being forced, don't install packages from the cache
                    NugetConfigFile.InstallFromCache = false;
                    readingSources = true;
                    useCommandLineSources = true;
                }
            }

            // if there are not command line overrides, use the NuGet.config package sources
            if (!useCommandLineSources)
            {
                if (NugetConfigFile.ActivePackageSource.ExpandedPath == "(Aggregate source)")
                {
                    packageSources.AddRange(NugetConfigFile.PackageSources);
                }
                else
                {
                    packageSources.Add(NugetConfigFile.ActivePackageSource);
                }
            }
        }

        /// <summary>
        /// Runs nuget.exe using the given arguments.
        /// </summary>
        /// <param name="arguments">The arguments to run nuget.exe with.</param>
        /// <param name="logOuput">True to output debug information to the Unity console.  Defaults to true.</param>
        /// <returns>The string of text that was output from nuget.exe following its execution.</returns>
        private static void RunNugetProcess(string arguments, bool logOuput = true)
        {
            // Try to find any nuget.exe in the package tools installation location
            string toolsPackagesFolder = Path.Combine(Application.dataPath, "../Packages");

            // create the folder to prevent an exception when getting the files
            Directory.CreateDirectory(toolsPackagesFolder);

            string[] files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
            if (files.Length > 1)
            {
                Debug.LogWarningFormat("More than one nuget.exe found. Using first one.");
            }
            else if (files.Length < 1)
            {
                Debug.LogWarningFormat("No nuget.exe found! Attemping to install the NuGet.CommandLine package.");
                InstallIdentifier(new NugetPackageIdentifier("NuGet.CommandLine", "2.8.6"));
                files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
                if (files.Length < 1)
                {
                    Debug.LogErrorFormat("nuget.exe still not found. Quiting...");
                    return;
                }
            }

            LogVerbose("Running: {0} \nArgs: {1}", files[0], arguments);

            string fileName = string.Empty;
            string commandLine = string.Empty;

#if UNITY_EDITOR_OSX
            // ATTENTION: you must install mono running on your mac, we use this mono to run `nuget.exe`
            fileName = "/Library/Frameworks/Mono.framework/Versions/Current/Commands/mono";
            commandLine = " " + files[0] + " " + arguments;
            LogVerbose("command: " + commandLine);
#else
            fileName = files[0];
            commandLine = arguments;
#endif
            Process process = Process.Start(
                new ProcessStartInfo(fileName, commandLine)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // WorkingDirectory = Path.GetDirectoryName(files[0]),

                    // http://stackoverflow.com/questions/16803748/how-to-decode-cmd-output-correctly
                    // Default = 65533, ASCII = ?, Unicode = nothing works at all, UTF-8 = 65533, UTF-7 = 242 = WORKS!, UTF-32 = nothing works at all
                    StandardOutputEncoding = Encoding.GetEncoding(850)
                });

            if (!process.WaitForExit(TimeOut))
            {
                Debug.LogWarning("NuGet took too long to finish.  Killing operation.");
                process.Kill();
            }

            string error = process.StandardError.ReadToEnd();
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError(error);
            }

            string output = process.StandardOutput.ReadToEnd();
            if (logOuput && !string.IsNullOrEmpty(output))
            {
                Debug.Log(output);
            }
        }

	    /// <summary>
		/// Copy the files and folders that we need from the extracted NuGet source to the Unity Assets/Packages folder.
		/// </summary>
		/// <param name="package"></param>
		/// <param name="packagePath"></param>
		private static void CopyPackageContentsToUnity(NugetPackageIdentifier package, string packagePath)
		{
			string packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, $"{package.Id}.{package.Version}");

			LogVerbose("Copying package to {0}", packageInstallDirectory);

			if (Directory.Exists(Path.Combine(packagePath, "lib")))
			{
				int intDotNetVersion = (int)DotNetVersion; // c
														   //bool using46 = DotNetVersion == ApiCompatibilityLevel.NET_4_6; // NET_4_6 option was added in Unity 5.6
				bool using46 = intDotNetVersion == 3; // NET_4_6 = 3 in Unity 5.6 and Unity 2017.1 - use the hard-coded int value to ensure it works in earlier versions of Unity
				bool usingStandard2 = intDotNetVersion == 6; // using .net standard 2.0                

				var selectedDirectories = new List<string>();

				// go through the library folders in descending order (highest to lowest version)
				var libDirectories = Directory.EnumerateDirectories(packagePath + "/lib").Select(s => new DirectoryInfo(s)).OrderByDescending(di => di.Name.ToLower()).ToList();

				// if there are no subdirectories in the lib folder, just take the folder itself
				if (libDirectories.Count == 0)
				{
					selectedDirectories.Add(Path.Combine(packagePath, "lib"));
				}

				foreach (var directory in libDirectories)
				{
					string directoryName = directory.Name.ToLower();

					// Select the highest .NET library available that is supported
					// See here: https://docs.nuget.org/ndocs/schema/target-frameworks
					if (usingStandard2 && directoryName == "netstandard2.0")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.6")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net462")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.5")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net461")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.4")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net46")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.3")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net452")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net451")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.2")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net45")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName.Contains("portable-net45"))
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.1")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "netstandard1.1")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.0")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net403")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && (directoryName == "net40" || directoryName == "net4"))
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (
						directoryName == "unity" ||
						directoryName == "net35-unity full v3.5" ||
						directoryName == "net35-unity subset v3.5")
					{
						// Keep all directories targeting Unity within a package
						selectedDirectories.Add(Path.Combine(directory.Parent.FullName, "unity"));
						selectedDirectories.Add(Path.Combine(directory.Parent.FullName, "net35-unity full v3.5"));
						selectedDirectories.Add(Path.Combine(directory.Parent.FullName, "net35-unity subset v3.5"));
						break;
					}
					else if (directoryName == "net35")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (directoryName == "net20")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (directoryName == "net11")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
				}

				foreach (var directory in selectedDirectories)
				{
					LogVerbose("Using {0}", directory);
					FileSystemHelpers.Copy(Path.Combine(packagePath, directory), 
						Path.Combine(packageInstallDirectory, "lib", libDirectories.Count == 0 ? "" : Path.GetFileNameWithoutExtension(directory)));
				}
			}

			if (Directory.Exists(Path.Combine(packagePath, "tools")))
			{
				// Move the tools folder outside of the Unity Assets folder
				string toolsInstallDirectory = Path.Combine(Application.dataPath, "..", $"Packages", $"{package.Id}.{package.Version}", "tools");

				LogVerbose("Moving {0} to {1}", packageInstallDirectory + "/tools", toolsInstallDirectory);

				FileSystemHelpers.Copy(Path.Combine(packagePath, "tools"), toolsInstallDirectory);
			}

			// delete all PDB files since Unity uses Mono and requires MDB files, which causes it to output "missing MDB" errors
			DeleteAllFiles(packageInstallDirectory, "*.pdb");

			// if there are native DLLs, copy them to the Unity project root (1 up from Assets)
			if (Directory.Exists(packagePath + "/output"))
				FileSystemHelpers.Copy(Path.Combine(packagePath, "output"), Directory.GetCurrentDirectory());

			// if there are Unity plugin DLLs, copy them to the Unity Plugins folder (Assets/Plugins)
			if (Directory.Exists(packagePath + "/unityplugin"))
				FileSystemHelpers.Copy(Path.Combine(packagePath, "unityplugin"), Path.Combine(Application.dataPath, "Plugins"));

			// if there are Unity StreamingAssets, copy them to the Unity StreamingAssets folder (Assets/StreamingAssets)
			if (Directory.Exists(packagePath + "/StreamingAssets"))
				FileSystemHelpers.Copy(Path.Combine(packagePath, "StreamingAssets"), Path.Combine(Application.dataPath, "StreamingAssets"));
		}

        /// <summary>
        /// Calls "nuget.exe pack" to create a .nupkg file based on the given .nuspec file.
        /// </summary>
        /// <param name="nuspecFilePath">The full filepath to the .nuspec file to use.</param>
        public static void Pack(string nuspecFilePath)
        {
            if (!Directory.Exists(PackOutputDirectory))
            {
                Directory.CreateDirectory(PackOutputDirectory);
            }

            // Use -NoDefaultExcludes to allow files and folders that start with a . to be packed into the package
            // This is done because if you want a file/folder in a Unity project, but you want Unity to ignore it, it must start with a .
            // This is especially useful for .cs and .js files that you don't want Unity to compile as game scripts
            string arguments = string.Format("pack \"{0}\" -OutputDirectory \"{1}\" -NoDefaultExcludes", nuspecFilePath, PackOutputDirectory);

            RunNugetProcess(arguments);
        }

        /// <summary>
        /// Calls "nuget.exe push" to push a .nupkf file to the the server location defined in the NuGet.config file.
        /// Note: This differs slightly from NuGet's Push command by automatically calling Pack if the .nupkg doesn't already exist.
        /// </summary>
        /// <param name="nuspec">The NuspecFile which defines the package to push.  Only the ID and Version are used.</param>
        /// <param name="nuspecFilePath">The full filepath to the .nuspec file to use.  This is required by NuGet's Push command.</param>
        /// /// <param name="apiKey">The API key to use when pushing a package to the server.  This is optional.</param>
        public static void Push(NuspecFile nuspec, string nuspecFilePath, string apiKey = "")
        {
            string packagePath = Path.Combine(PackOutputDirectory, string.Format("{0}.{1}.nupkg", nuspec.Id, nuspec.Version));
            if (!File.Exists(packagePath))
            {
                LogVerbose("Attempting to Pack.");
                Pack(nuspecFilePath);

                if (!File.Exists(packagePath))
                {
                    Debug.LogErrorFormat("NuGet package not found: {0}", packagePath);
                    return;
                }
            }

            string arguments = string.Format("push \"{0}\" {1} -configfile \"{2}\"", packagePath, apiKey, NugetConfigFilePath);

            RunNugetProcess(arguments);
        }

        /// <summary>
        /// Recursively deletes the folder at the given path.
        /// NOTE: Directory.Delete() doesn't delete Read-Only files, whereas this does.
        /// </summary>
        /// <param name="directoryPath">The path of the folder to delete.</param>
        private static void DeleteDirectory(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return;

            var directoryInfo = new DirectoryInfo(directoryPath);

            // delete any sub-folders first
            foreach (var childInfo in directoryInfo.GetFileSystemInfos())
            {
                DeleteDirectory(childInfo.FullName);
            }

            // remove the read-only flag on all files
            var files = directoryInfo.GetFiles();
            foreach (var file in files)
            {
                file.Attributes = FileAttributes.Normal;
            }

            // remove the read-only flag on the directory
            directoryInfo.Attributes = FileAttributes.Normal;

            // recursively delete the directory
            directoryInfo.Delete(true);
        }

        /// <summary>
        /// Deletes a file at the given filepath.
        /// </summary>
        /// <param name="filePath">The filepath to the file to delete.</param>
        private static void DeleteFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
                File.Delete(filePath);
            }
        }

        /// <summary>
        /// Deletes all files in the given directory or in any sub-directory, with the given extension.
        /// </summary>
        /// <param name="directoryPath">The path to the directory to delete all files of the given extension from.</param>
        /// <param name="extension">The extension of the files to delete, in the form "*.ext"</param>
        private static void DeleteAllFiles(string directoryPath, string extension)
        {
	        if (Directory.Exists(directoryPath) == false)
		        return;

            string[] files = Directory.GetFiles(directoryPath, extension, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                DeleteFile(file);
            }
        }

        /// <summary>
        /// Uninstalls all of the currently installed packages.
        /// </summary>
        internal static void UninstallAll()
        {
	        for (var i = PackagesConfigFile.Packages.Count - 1; i >= 0; i--)
	        {
		        var package = PackagesConfigFile.Packages[i];
		        Uninstall(package);
	        }
        }

        /// <summary>
        /// "Uninstalls" the given package by simply deleting its folder.
        /// </summary>
        /// <param name="package">The NugetPackage to uninstall.</param>
        /// <param name="refreshAssets">True to force Unity to refesh its Assets folder.  False to temporarily ignore the change.  Defaults to true.</param>
        public static void Uninstall(NugetPackageIdentifier package, bool refreshAssets = true)
        {
            LogVerbose("Uninstalling: {0} {1}", package.Id, package.Version);

            // update the package.config file
            PackagesConfigFile.RemovePackage(package);
            PackagesConfigFile.Save(PackagesConfigFilePath);

            string packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, string.Format("{0}.{1}", package.Id, package.Version));
            DeleteDirectory(packageInstallDirectory);

            string metaFile = Path.Combine(NugetConfigFile.RepositoryPath, string.Format("{0}.{1}.meta", package.Id, package.Version));
            DeleteFile(metaFile);

            string toolsInstallDirectory = Path.Combine(Application.dataPath, string.Format("../Packages/{0}.{1}", package.Id, package.Version));
            DeleteDirectory(toolsInstallDirectory);

			RefreshPackageConfig();
            if (refreshAssets)
                AssetDatabase.Refresh();
        }

        /// <summary>
        /// Updates a package by uninstalling the currently installed version and installing the "new" version.
        /// </summary>
        /// <param name="currentVersion">The current package to uninstall.</param>
        /// <param name="newVersion">The package to install.</param>
        /// <param name="refreshAssets">True to refresh the assets inside Unity.  False to ignore them (for now).  Defaults to true.</param>
        public static bool Update(NugetPackageIdentifier currentVersion, NugetPackage newVersion, bool refreshAssets = true)
        {
            LogVerbose("Updating {0} {1} to {2}", currentVersion.Id, currentVersion.Version, newVersion.Version);
            Uninstall(currentVersion, false);
            return InstallIdentifier(newVersion, refreshAssets);
        }

        /// <summary>
        /// Installs all of the given updates, and uninstalls the corresponding package that is already installed.
        /// </summary>
        /// <param name="updates">The list of all updates to install.</param>
        /// <param name="packagesToUpdate">The list of all packages currently installed.</param>
        public static void UpdateAll(IEnumerable<NugetPackage> updates, List<NugetPackageIdentifier> packagesToUpdate)
        {
            float progressStep = 1.0f / updates.Count();
            float currentProgress = 0;

            foreach (NugetPackage update in updates)
            {
                EditorUtility.DisplayProgressBar(string.Format("Updating to {0} {1}", update.Id, update.Version), "Installing All Updates", currentProgress);

                NugetPackageIdentifier installedPackage = packagesToUpdate.FirstOrDefault(p => p.Id == update.Id);
                if (installedPackage != null)
                {
                    Update(installedPackage, update, false);
                }
                else
                {
                    Debug.LogErrorFormat("Trying to update {0} to {1}, but no version is installed!", update.Id, update.Version);
                }

                currentProgress += progressStep;
            }

            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();
        }
		
        /// <summary>
        /// Gets a list of NuGetPackages via the HTTP Search() function defined by NuGet.Server and NuGet Gallery.
        /// This allows searching for partial IDs or even the empty string (the default) to list ALL packages.
        /// 
        /// NOTE: See the functions and parameters defined here: https://www.nuget.org/api/v2/$metadata
        /// </summary>
        /// <param name="searchTerm">The search term to use to filter packages. Defaults to the empty string.</param>
        /// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
        /// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
        /// <param name="numberToGet">The number of packages to fetch.</param>
        /// <param name="numberToSkip">The number of packages to skip before fetching.</param>
        /// <returns>The list of available packages.</returns>
        public static List<NugetPackage> Search(string searchTerm = "", bool includeAllVersions = false, bool includePrerelease = false, int numberToGet = 15, int numberToSkip = 0)
        {
            List<NugetPackage> packages = new List<NugetPackage>();

            // Loop through all active sources and combine them into a single list
            foreach (var source in packageSources.Where(s => s.IsEnabled))
            {
                var newPackages = source.Search(searchTerm, includeAllVersions, includePrerelease, numberToGet, numberToSkip);
                packages.AddRange(newPackages);
                packages = packages.Distinct().ToList();
            }

            return packages;
        }

        /// <summary>
        /// Queries the server with the given list of installed packages to get any updates that are available.
        /// </summary>
        /// <param name="packagesToUpdate">The list of currently installed packages.</param>
        /// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
        /// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
        /// <param name="targetFrameworks">The specific frameworks to target?</param>
        /// <param name="versionContraints">The version constraints?</param>
        /// <returns>A list of all updates available.</returns>
        public static List<NugetPackage> GetUpdates(List<NugetPackageIdentifier> packagesToUpdate, bool includePrerelease = false, bool includeAllVersions = false, string targetFrameworks = "", string versionContraints = "")
        {
            List<NugetPackage> packages = new List<NugetPackage>();

            // Loop through all active sources and combine them into a single list
            foreach (var source in packageSources.Where(s => s.IsEnabled))
            {
                var newPackages = source.GetUpdates(packagesToUpdate, includePrerelease, includeAllVersions, targetFrameworks, versionContraints);
                packages.AddRange(newPackages);
                packages = packages.Distinct().ToList();
            }

            return packages;
        }

        /// <summary>
        /// Gets a NugetPackage from the NuGet server with the exact ID and Version given.
        /// If an exact match isn't found, it selects the next closest version available.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier"/> containing the ID and Version of the package to get.</param>
        /// <returns>The retrieved package, if there is one.  Null if no matching package was found.</returns>
        private static NugetPackage GetSpecificPackage(NugetPackageIdentifier packageId)
        {
	        if (NugetHelper.NugetConfigFile.InstallFromCache && IsPackageInstalled(packageId))
	        {
		        var package = GetCachedPackage(packageId);
		        if (package != null)
			        return package;
	        }

	        return GetOnlinePackage(packageId);
        }

        /// <summary>
        /// Tries to find an already installed package that matches (or is in the range of) the given package ID.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier"/> of the <see cref="NugetPackage"/> to find.</param>
        /// <returns>The best <see cref="NugetPackage"/> match, if there is one, otherwise null.</returns>
        public static bool IsPackageInstalled(NugetPackageIdentifier packageId)
        {
	        var installedPackage = GetInstalledPackage(packageId);

	        if (installedPackage == null)
		        return false;

	        if (packageId.Version != installedPackage.Version)
			{
				if (packageId.InRange(installedPackage))
				{
					LogVerbose("Requested {0} {1}, but {2} is already installed, so using that.", packageId.Id, packageId.Version, installedPackage.Version);
				}
				else
				{
					LogVerbose("Requested {0} {1}. {2} is already installed, but it is out of range.", packageId.Id, packageId.Version, installedPackage.Version);
					installedPackage = null;
				}
			}
			else
			{
				LogVerbose("Found exact package already installed: {0} {1}", installedPackage.Id, installedPackage.Version);
			}
			
			return installedPackage != null;
        }

        private static NugetPackageIdentifier GetInstalledPackage(NugetPackageIdentifier packageId)
        {
	        var installedPackage = PackagesConfigFile.Packages.FirstOrDefault(identifier => identifier.Id == packageId.Id);
	        return installedPackage;
        }

        /// <summary>
        /// Tries to find an already cached package that matches the given package ID.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier"/> of the <see cref="NugetPackage"/> to find.</param>
        /// <returns>The best <see cref="NugetPackage"/> match, if there is one, otherwise null.</returns>
        public static NugetPackage GetCachedPackage(NugetPackageIdentifier packageId)
        {
            string cachedPackagePath = GetCachedPackagePath(packageId);

            if (File.Exists(cachedPackagePath))
            {
                LogVerbose("Found exact package in the cache: {0}", cachedPackagePath);
                return NugetPackage.FromNupkgFile(cachedPackagePath);
            }

            return null;
        }

        /// <summary>
        /// Tries to find an "online" (in the package sources - which could be local) package that matches (or is in the range of) the given package ID.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier"/> of the <see cref="NugetPackage"/> to find.</param>
        /// <returns>The best <see cref="NugetPackage"/> match, if there is one, otherwise null.</returns>
        private static NugetPackage GetOnlinePackage(NugetPackageIdentifier packageId)
        {
            NugetPackage package = null;

            // Loop through all active sources and stop once the package is found
            foreach (var source in packageSources.Where(s => s.IsEnabled))
            {
                var foundPackage = source.GetSpecificPackage(packageId);
                if (foundPackage == null)
                {
                    continue;
                }

                if (foundPackage.Version == packageId.Version)
                {
                    LogVerbose("{0} {1} was found in {2}", foundPackage.Id, foundPackage.Version, source.Name);
                    return foundPackage;
                }

                LogVerbose("{0} {1} was found in {2}, but wanted {3}", foundPackage.Id, foundPackage.Version, source.Name, packageId.Version);
                if (package == null)
                {
                    // if another package hasn't been found yet, use the current found one
                    package = foundPackage;
                }
                // another package has been found previously, but neither match identically
                else if (foundPackage > package)
                {
                    // use the new package if it's closer to the desired version
                    package = foundPackage;
                }
            }
            if (package != null)
            {
                LogVerbose("{0} {1} not found, using {2}", packageId.Id, packageId.Version, package.Version);
            }
            else
            {
                LogVerbose("Failed to find {0} {1}", packageId.Id, packageId.Version);
            }

            return package;
        }

        /// <summary>
        /// Copies the contents of input to output. Doesn't close either stream.
        /// </summary>
        private static void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[8 * 1024];
            int len;
            while ((len = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, len);
            }
        }

        /// <summary>
        /// Installs the package given by the identifer.  It fetches the appropriate full package from the installed packages, package cache, or package sources and installs it.
        /// </summary>
        /// <param name="package">The identifer of the package to install.</param>
        /// <param name="refreshAssets">True to refresh the Unity asset database.  False to ignore the changes (temporarily).</param>
        internal static bool InstallIdentifier(NugetPackageIdentifier package, bool refreshAssets = true)
        {
            NugetPackage foundPackage = GetSpecificPackage(package);

            if (foundPackage != null)
            {
                return Install(foundPackage, refreshAssets);
            }
            else
            {
                Debug.LogErrorFormat("Could not find {0} {1} or greater.", package.Id, package.Version);
                return false;
            }
        }

        /// <summary>
        /// Outputs the given message to the log only if verbose mode is active.  Otherwise it does nothing.
        /// </summary>
        /// <param name="format">The formatted message string.</param>
        /// <param name="args">The arguments for the formattted message string.</param>
        public static void LogVerbose(string format, params object[] args)
        {
            if (NugetConfigFile.Verbose)
            {
#if UNITY_5_4_OR_NEWER
                var stackTraceLogType = Application.GetStackTraceLogType(LogType.Log);
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
#else
                var stackTraceLogType = Application.stackTraceLogType;
                Application.stackTraceLogType = StackTraceLogType.None;
#endif
                Debug.LogFormat(format, args);

#if UNITY_5_4_OR_NEWER
                Application.SetStackTraceLogType(LogType.Log, stackTraceLogType);
#else
                Application.stackTraceLogType = stackTraceLogType;
#endif
            }
        }

        /// <summary>
        /// Installs the given package.
        /// </summary>
        /// <param name="package">The package to install.</param>
        /// <param name="refreshAssets">True to refresh the Unity asset database.  False to ignore the changes (temporarily).</param>
        public static bool Install(NugetPackage package, bool refreshAssets = true)
        {
            NugetPackageIdentifier installedPackage = GetInstalledPackage(package);
            
            if (installedPackage != null)
            {
                if (installedPackage < package)
                {
                    LogVerbose("{0} {1} is installed, but need {2} or greater. Updating to {3}", installedPackage.Id, installedPackage.Version, package.Version, package.Version);
                    return Update(installedPackage, package, false);
                }

                if (installedPackage > package)
                {
	                LogVerbose("{0} {1} is installed. {2} or greater is needed, so using installed version.", installedPackage.Id, installedPackage.Version, package.Version);
                }
                else
                {
	                LogVerbose("Already installed: {0} {1}", package.Id, package.Version);
                }
                return true;
            }

            bool installSuccess = false;
            try
            {
                LogVerbose("Installing: {0} {1}", package.Id, package.Version);

                // look to see if the package (any version) is already installed


                if (refreshAssets)
                    EditorUtility.DisplayProgressBar(string.Format("Installing {0} {1}", package.Id, package.Version), "Installing Dependencies", 0.1f);

                // install all dependencies
                foreach (var dependency in package.Dependencies)
                {
                    LogVerbose("Installing Dependency: {0} {1}", dependency.Id, dependency.Version);
                    bool installed = InstallIdentifier(dependency);
                    if (!installed)
                    {
                        throw new Exception(String.Format("Failed to install dependency: {0} {1}.", dependency.Id, dependency.Version));
                    }
                }

                // update packages.config
                PackagesConfigFile.AddPackage(package);
                PackagesConfigFile.Save(PackagesConfigFilePath);

                string cachedPackagePath = GetCachedPackagePath(package);
                if (NugetConfigFile.InstallFromCache && File.Exists(cachedPackagePath))
                {
                    LogVerbose("Cached package found for {0} {1}", package.Id, package.Version);
                }
                else
                {
                    if (package.PackageSource.IsLocalPath)
                    {
                        LogVerbose("Caching local package {0} {1}", package.Id, package.Version);

                        // copy the .nupkg from the local path to the cache
                        File.Copy(Path.Combine(package.PackageSource.ExpandedPath, $"./{package.Id}.{package.Version}.nupkg"), cachedPackagePath, true);
                    }
                    else
                    {
                        // Mono doesn't have a Certificate Authority, so we have to provide all validation manually.  Currently just accept anything.
                        // See here: http://stackoverflow.com/questions/4926676/mono-webrequest-fails-with-https

                        // remove all handlers
                        //if (ServicePointManager.ServerCertificateValidationCallback != null)
                        //    foreach (var d in ServicePointManager.ServerCertificateValidationCallback.GetInvocationList())
                        //        ServicePointManager.ServerCertificateValidationCallback -= (d as System.Net.Security.RemoteCertificateValidationCallback);
                        ServicePointManager.ServerCertificateValidationCallback = null;

                        // add anonymous handler
                        ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, policyErrors) => true;

                        LogVerbose("Downloading package {0} {1}", package.Id, package.Version);

                        if (refreshAssets)
                            EditorUtility.DisplayProgressBar(string.Format("Installing {0} {1}", package.Id, package.Version), "Downloading Package", 0.3f);

                        Stream objStream = RequestUrl(package.DownloadUrl, package.PackageSource.UserName, package.PackageSource.ExpandedPassword, timeOut: null);
                        
                        if (Directory.Exists(Path.GetDirectoryName(cachedPackagePath)) == false)
	                        Directory.CreateDirectory(Path.GetDirectoryName(cachedPackagePath));

                        using (Stream file = File.Create(cachedPackagePath))
                        {
                            CopyStream(objStream, file);
                        }
                    }
                }

                if (refreshAssets)
                    EditorUtility.DisplayProgressBar(string.Format("Installing {0} {1}", package.Id, package.Version), "Extracting Package", 0.6f);

                if (File.Exists(cachedPackagePath))
                {
	                var packageDirectory = Path.GetDirectoryName(cachedPackagePath);

                    // unzip the package
                    using (ZipFile zip = ZipFile.Read(cachedPackagePath))
                    {
                        foreach (ZipEntry entry in zip)
                        {
                            entry.Extract(packageDirectory, ExtractExistingFileAction.OverwriteSilently);
                            if (NugetConfigFile.ReadOnlyPackageFiles)
                            {
                                FileInfo extractedFile = new FileInfo(Path.Combine(packageDirectory, entry.FileName));
                                extractedFile.Attributes |= FileAttributes.ReadOnly;
                            }
                        }
                    }
                }
                else
                {
                    Debug.LogErrorFormat("File not found: {0}", cachedPackagePath);
                }

                if (refreshAssets)
                    EditorUtility.DisplayProgressBar($"Installing {package.Id} {package.Version}", "Copying package", 0.9f);

                // clean
                CopyPackageContentsToUnity(package, Path.GetDirectoryName(cachedPackagePath));

                installSuccess = true;
            }
            catch (Exception e)
            {
                WarnIfDotNetAuthenticationIssue(e);
                Debug.LogErrorFormat("Unable to install package {0} {1}\n{2}", package.Id, package.Version, e.ToString());
                installSuccess = false;
            }
            finally
            {
                if (refreshAssets)
                {
                    EditorUtility.DisplayProgressBar($"Installing {package.Id} {package.Version}", "Importing Package", 0.95f);
                    AssetDatabase.Refresh();
                    EditorUtility.ClearProgressBar();
                }
            }
            return installSuccess;
        }

        private static string GetCachedPackagePath(NugetPackageIdentifier package)
        {
	        return Path.Combine(PackOutputDirectory, $"{package.Id}", $"{package.Version}", $"{package.Id}.{package.Version}.nupkg");
        }

        private static void WarnIfDotNetAuthenticationIssue(Exception e)
        {
#if !NET_4_6
            WebException webException = e as WebException;
            HttpWebResponse webResponse = webException != null ? webException.Response as HttpWebResponse : null;
            if (webResponse != null && webResponse.StatusCode == HttpStatusCode.BadRequest && webException.Message.Contains("Authentication information is not given in the correct format"))
            {
                // This error occurs when downloading a package with authentication using .NET 3.5, but seems to be fixed by the new .NET 4.6 runtime.
                // Inform users when this occurs.
                Debug.LogError("Authentication failed. This can occur due to a known issue in .NET 3.5. This can be fixed by changing Scripting Runtime to Experimental (.NET 4.6 Equivalent) in Player Settings.");
            }
#endif
        }

        private struct AuthenticatedFeed
        {
            public string AccountUrlPattern;
            public string ProviderUrlTemplate;

            public string GetAccount(string url)
            {
                Match match = Regex.Match(url, AccountUrlPattern, RegexOptions.IgnoreCase);
                if (!match.Success) { return null; }

                return match.Groups["account"].Value;
            }

            public string GetProviderUrl(string account)
            {
                return ProviderUrlTemplate.Replace("{account}", account);
            }
        }

        // TODO: Move to ScriptableObjet
        private static List<AuthenticatedFeed> knownAuthenticatedFeeds = new List<AuthenticatedFeed>()
        {
            new AuthenticatedFeed()
            {
                AccountUrlPattern = @"^https:\/\/(?<account>[a-zA-z0-9]+).pkgs.visualstudio.com",
                ProviderUrlTemplate = "https://{account}.pkgs.visualstudio.com/_apis/public/nuget/client/CredentialProviderBundle.zip"
            },
            new AuthenticatedFeed()
            {
                AccountUrlPattern = @"^https:\/\/pkgs.dev.azure.com\/(?<account>[a-zA-z0-9]+)\/",
                ProviderUrlTemplate = "https://pkgs.dev.azure.com/{account}/_apis/public/nuget/client/CredentialProviderBundle.zip"
            }
        };

        /// <summary>
        /// Get the specified URL from the web. Throws exceptions if the request fails.
        /// </summary>
        /// <param name="url">URL that will be loaded.</param>
        /// <param name="password">Password that will be passed in the Authorization header or the request. If null, authorization is omitted.</param>
        /// <param name="timeOut">Timeout in milliseconds or null to use the default timeout values of HttpWebRequest.</param>
        /// <returns>Stream containing the result.</returns>
        public static Stream RequestUrl(string url, string userName, string password, int? timeOut)
        {
            HttpWebRequest getRequest = (HttpWebRequest)WebRequest.Create(url);
            if (timeOut.HasValue)
            {
                getRequest.Timeout = timeOut.Value;
                getRequest.ReadWriteTimeout = timeOut.Value;
            }

            if (string.IsNullOrEmpty(password))
            {
                CredentialProviderResponse? creds = GetCredentialFromProvider(GetTruncatedFeedUri(getRequest.RequestUri));
                if (creds.HasValue)
                {
                    userName = creds.Value.Username;
                    password = creds.Value.Password;
                }
            }

            if (password != null)
            {
                // Send password as described by https://docs.microsoft.com/en-us/vsts/integrate/get-started/rest/basics.
                // This works with Visual Studio Team Services, but hasn't been tested with other authentication schemes so there may be additional work needed if there
                // are different kinds of authentication.
                getRequest.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(System.Text.ASCIIEncoding.ASCII.GetBytes(string.Format("{0}:{1}", userName, password))));
            }

            LogVerbose("HTTP GET {0}", url);
            Stream objStream = getRequest.GetResponse().GetResponseStream();
            return objStream;
        }

        /// <summary>
        /// Restores all packages defined in packages.config.
        /// </summary>
        public static void Restore()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            RefreshPackageConfig();

			try
            {
                float progressStep = 1.0f / PackagesConfigFile.Packages.Count;
                float currentProgress = 0;

                // copy the list since the InstallIdentifier operation below changes the actual installed packages list
                var packagesToInstall = new List<NugetPackageIdentifier>(PackagesConfigFile.Packages);

                LogVerbose("Restoring {0} packages.", packagesToInstall.Count);

                foreach (var package in packagesToInstall)
                {
                    if (package != null)
                    {
                        EditorUtility.DisplayProgressBar("Restoring NuGet Packages", string.Format("Restoring {0} {1}", package.Id, package.Version), currentProgress);

                        if (!IsPackageInstalled(package))
                        {
                            LogVerbose("---Restoring {0} {1}", package.Id, package.Version);
                            InstallIdentifier(package);
                        }
                        else
                        {
                            LogVerbose("---Already installed: {0} {1}", package.Id, package.Version);
                        }
                    }

                    currentProgress += progressStep;
                }

                CheckForUnnecessaryPackages();
            }
            catch (Exception e)
            {
                Debug.LogErrorFormat("{0}", e.ToString());
            }
            finally
            {
                stopwatch.Stop();
                LogVerbose("Restoring packages took {0} ms", stopwatch.ElapsedMilliseconds);

                AssetDatabase.Refresh();
                EditorUtility.ClearProgressBar();
            }
        }

        public static void RefreshPackageConfig()
        {
	        packagesConfigFile = PackagesConfigFile.Load(PackagesConfigFilePath);
        }

		internal static void CheckForUnnecessaryPackages()
        {
            if (!Directory.Exists(NugetConfigFile.RepositoryPath))
                return;

            var directories = Directory.GetDirectories(NugetConfigFile.RepositoryPath, "*", SearchOption.TopDirectoryOnly);
            foreach (var folder in directories)
            {
                var name = Path.GetFileName(folder);
                var installed = false;
                foreach (var package in PackagesConfigFile.Packages)
                {
                    var packageName = string.Format("{0}.{1}", package.Id, package.Version);
                    if (name == packageName)
                    {
                        installed = true;
                        break;
                    }
                }
                if (!installed)
                {
                    LogVerbose("---DELETE unnecessary package {0}", name);

                    DeleteDirectory(folder);
                    DeleteFile(folder + ".meta");
                }
            }

        }

        /// <summary>
        /// Data class returned from nuget credential providers in a JSON format. As described here:
        /// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers#creating-a-nugetexe-credential-provider
        /// </summary>
        [System.Serializable]
        private struct CredentialProviderResponse
        {
            public string Username;
            public string Password;
        }

        /// <summary>
        /// Possible response codes returned by a Nuget credential provider as described here:
        /// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers#creating-a-nugetexe-credential-provider
        /// </summary>
        private enum CredentialProviderExitCode
        {
            Success = 0,
            ProviderNotApplicable = 1,
            Failure = 2
        }

        private static void DownloadCredentialProviders(Uri feedUri)
        {
            foreach (var feed in NugetHelper.knownAuthenticatedFeeds)
            {
                string account = feed.GetAccount(feedUri.ToString());
                if (string.IsNullOrEmpty(account)) { continue; }

                string providerUrl = feed.GetProviderUrl(account);

                HttpWebRequest credentialProviderRequest = (HttpWebRequest)WebRequest.Create(providerUrl);

                try
                {
                    Stream credentialProviderDownloadStream = credentialProviderRequest.GetResponse().GetResponseStream();

                    string tempFileName = Path.GetTempFileName();
                    LogVerbose("Writing {0} to {1}", providerUrl, tempFileName);

                    using (FileStream file = File.Create(tempFileName))
                    {
                        CopyStream(credentialProviderDownloadStream, file);
                    }

                    string providerDestination = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDERS_PATH");
                    if (String.IsNullOrEmpty(providerDestination))
                    {
                        providerDestination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuget/CredentialProviders");
                    }

                    // Unzip the bundle and extract any credential provider exes
                    using (ZipFile zip = ZipFile.Read(tempFileName))
                    {
                        foreach (ZipEntry entry in zip)
                        {
                            if (Regex.IsMatch(entry.FileName, @"^credentialprovider.+\.exe$", RegexOptions.IgnoreCase))
                            {
                                LogVerbose("Extracting {0} to {1}", entry.FileName, providerDestination);
                                entry.Extract(providerDestination, ExtractExistingFileAction.OverwriteSilently);
                            }
                        }
                    }

                    // Delete the bundle
                    File.Delete(tempFileName);
                }
                catch (Exception e)
                {
                    Debug.LogErrorFormat("Failed to download credential provider from {0}: {1}", credentialProviderRequest.Address, e.Message);
                }
            }

        }

        /// <summary>
        /// Helper function to aquire a token to access VSTS hosted nuget feeds by using the CredentialProvider.VSS.exe
        /// tool. Downloading it from the VSTS instance if needed.
        /// See here for more info on nuget Credential Providers:
        /// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers
        /// </summary>
        /// <param name="feedUri">The hostname where the VSTS instance is hosted (such as microsoft.pkgs.visualsudio.com.</param>
        /// <returns>The password in the form of a token, or null if the password could not be aquired</returns>
        private static CredentialProviderResponse? GetCredentialFromProvider(Uri feedUri)
        {
            CredentialProviderResponse? response;
            if (!cachedCredentialsByFeedUri.TryGetValue(feedUri, out response))
            {
                response = GetCredentialFromProvider_Uncached(feedUri, true);
                cachedCredentialsByFeedUri[feedUri] = response;
            }
            return response;
        }

        /// <summary>
        /// Given the URI of a nuget method, returns the URI of the feed itself without the method and query parameters.
        /// </summary>
        /// <param name="methodUri">URI of nuget method.</param>
        /// <returns>URI of the feed without the method and query parameters.</returns>
        private static Uri GetTruncatedFeedUri(Uri methodUri)
        {
            string truncatedUriString = methodUri.GetLeftPart(UriPartial.Path);

            // Pull off the function if there is one
            if (truncatedUriString.EndsWith(")"))
            {
                int lastSeparatorIndex = truncatedUriString.LastIndexOf('/');
                if (lastSeparatorIndex != -1)
                {
                    truncatedUriString = truncatedUriString.Substring(0, lastSeparatorIndex);
                }
            }
            Uri truncatedUri = new Uri(truncatedUriString);
            return truncatedUri;
        }

        /// <summary>
        /// Clears static credentials previously cached by GetCredentialFromProvider.
        /// </summary>
        public static void ClearCachedCredentials()
        {
            cachedCredentialsByFeedUri.Clear();
        }

        /// <summary>
        /// Internal function called by GetCredentialFromProvider to implement retrieving credentials. For performance reasons,
        /// most functions should call GetCredentialFromProvider in order to take advantage of cached credentials.
        /// </summary>
        private static CredentialProviderResponse? GetCredentialFromProvider_Uncached(Uri feedUri, bool downloadIfMissing)
        {
            LogVerbose("Getting credential for {0}", feedUri);

            // Build the list of possible locations to find the credential provider. In order it should be local app data, paths set on the
            // environment varaible, and lastly look at the root of the pacakges save location.
            List<string> possibleCredentialProviderPaths = new List<string>();
            possibleCredentialProviderPaths.Add(Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuget"), "CredentialProviders"));

            string environmentCredentialProviderPaths = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDERS_PATH");
            if (!string.IsNullOrEmpty(environmentCredentialProviderPaths))
            {
                possibleCredentialProviderPaths.AddRange(
                    environmentCredentialProviderPaths.Split(new string[] { ";" }, StringSplitOptions.RemoveEmptyEntries) ?? Enumerable.Empty<string>());
            }

            // Try to find any nuget.exe in the package tools installation location
            string toolsPackagesFolder = Path.Combine(Application.dataPath, "../Packages");
            possibleCredentialProviderPaths.Add(toolsPackagesFolder);

            // Search through all possible paths to find the credential provider.
            var providerPaths = new List<string>();
            foreach (string possiblePath in possibleCredentialProviderPaths)
            {
                if (Directory.Exists(possiblePath))
                {
                    providerPaths.AddRange(Directory.GetFiles(possiblePath, "credentialprovider*.exe", SearchOption.AllDirectories));
                }
            }

            foreach (var providerPath in providerPaths.Distinct())
            {
                // Launch the credential provider executable and get the json encoded response from the std output
                Process process = new Process();
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.FileName = providerPath;
                process.StartInfo.Arguments = string.Format("-uri \"{0}\"", feedUri.ToString());

                // http://stackoverflow.com/questions/16803748/how-to-decode-cmd-output-correctly
                // Default = 65533, ASCII = ?, Unicode = nothing works at all, UTF-8 = 65533, UTF-7 = 242 = WORKS!, UTF-32 = nothing works at all
                process.StartInfo.StandardOutputEncoding = Encoding.GetEncoding(850);
                process.Start();
                process.WaitForExit();

                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();

                switch ((CredentialProviderExitCode)process.ExitCode)
                {
                    case CredentialProviderExitCode.ProviderNotApplicable: break; // Not the right provider
                    case CredentialProviderExitCode.Failure: // Right provider, failure to get creds
                        {
                            Debug.LogErrorFormat("Failed to get credentials from {0}!\n\tOutput\n\t{1}\n\tErrors\n\t{2}", providerPath, output, errors);
                            return null;
                        }
                    case CredentialProviderExitCode.Success:
                        {
                            return JsonUtility.FromJson<CredentialProviderResponse>(output);
                        }
                    default:
                        {
                            Debug.LogWarningFormat("Unrecognized exit code {0} from {1} {2}", process.ExitCode, providerPath, process.StartInfo.Arguments);
                            break;
                        }
                }
            }

            if(downloadIfMissing)
            {
                DownloadCredentialProviders(feedUri);
                return GetCredentialFromProvider_Uncached(feedUri, false);
            }

            return null;
        }
    }
}
