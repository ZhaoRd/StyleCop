//-----------------------------------------------------------------------
// <copyright file="StyleCopCore.cs">
//   MS-PL
// </copyright>
// <license>
//   This source code is subject to terms and conditions of the Microsoft 
//   Public License. A copy of the license can be found in the License.html 
//   file at the root of this distribution. 
//   By using this source code in any fashion, you are agreeing to be bound 
//   by the terms of the Microsoft Public License. You must not remove this 
//   notice, or any other, from this software.
// </license>
//-----------------------------------------------------------------------
namespace StyleCop
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.IO;
    using System.Reflection;
    using System.Security;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;
    using System.Xml;
    using Microsoft.Build.Framework;
    using Microsoft.Win32;

    /// <summary>
    /// The main entrypoint into the StyleCop core module.
    /// </summary>
    [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "StyleCop", Justification = "This is the correct casing.")]
    public sealed class StyleCopCore : IPropertyContainer, IDisposable
    {
        #region Internal Constants

        /// <summary>
        /// The name of the ID for the project settings property page dialog.
        /// </summary>
        internal const string ProjectSettingsPropertyPageIdProperty = "StyleCopLocalProperties";

        #endregion Internal Constants

        #region Private Fields

        /// <summary>
        /// Indicates whether the module is currently analying files.
        /// </summary>
        private bool analyzing;

        /// <summary>
        /// True if the analyze should be canceled.
        /// </summary>
        private bool cancel;

        /// <summary>
        /// The public context for the current analysis run.
        /// </summary>
        private RunContext runContext;

        /// <summary>
        /// Indicates whether the results cache should be saved for each analyzed source code document.
        /// </summary>
        private bool writeResultsCache = true;

        /// <summary>
        /// Indicates whether it is ok to display UI dialogs.
        /// </summary>
        private bool displayUI = true;

        /// <summary>
        /// The list of loaded code parsers.
        /// </summary>
        private Dictionary<string, SourceParser> parsers = new Dictionary<string, SourceParser>();

        /// <summary>
        /// The list of loaded analyzers.
        /// </summary>
        private Dictionary<string, SourceAnalyzer> analyzers = new Dictionary<string, SourceAnalyzer>();

        /// <summary>
        /// The environment that StyleCop is running under.
        /// </summary>
        private StyleCopEnvironment environment;

        /// <summary>
        /// The registry manager.
        /// </summary>
        private RegistryUtils registry = new RegistryUtils();

        /// <summary>
        /// The fake core parser.
        /// </summary>
        private CoreParser coreParser = new CoreParser();

        /// <summary>
        /// A tag object which can be optionally filled in by the host.
        /// </summary>
        private object hostTag;

        /// <summary>
        /// The file logger.
        /// </summary>
        private Log log;

        #endregion Private Fields

        #region Public Constructors

        /// <summary>
        /// Initializes a new instance of the StyleCopCore class.
        /// </summary>
        public StyleCopCore() : this(null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the StyleCopCore class.
        /// </summary>
        /// <param name="environment">The environment that StyleCop is running under.</param>
        public StyleCopCore(StyleCopEnvironment environment) : this(environment, null)
        {
            Param.Ignore(environment);
        }

        /// <summary>
        /// Initializes a new instance of the StyleCopCore class.
        /// </summary>
        /// <param name="environment">The environment that StyleCop is running under.</param>
        /// <param name="hostTag">A tag object which can be optionally filled in by the host.</param>
        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times", Justification = "This is safe")]
        public StyleCopCore(StyleCopEnvironment environment, object hostTag)
        {
            Param.Ignore(environment);
            Param.Ignore(hostTag);

            this.environment = environment;
            this.hostTag = hostTag;

            // If no environment was provided, use the file based environment.
            if (this.environment == null)
            {
                this.environment = new FileBasedEnvironment();
            }

            this.environment.Core = this;

            // Set up the logger.
            this.log = new Log(this);

            // Load the core xml initialization document.
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StyleCop.CoreParser.xml"))
                using (StreamReader reader = new StreamReader(stream))
                {
                    string xml = reader.ReadToEnd();
                    XmlDocument parserXml = new XmlDocument();
                    parserXml.LoadXml(xml);
                    this.coreParser.Initialize(this, parserXml, true, true);
                }
            }
            catch (XmlException)
            {
                AlertDialog.Show(
                    this,
                    null,
                    Strings.StyleCopUnableToLoad,
                    Strings.Title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (ArgumentException argex)
            {
                AlertDialog.Show(
                    this,
                    null,
                    string.Format(CultureInfo.CurrentUICulture, Strings.StyleCopUnableToLoad, argex.Message),
                    Strings.Title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        #endregion Public Constructors

        #region Public Events

        /// <summary>
        /// Event that is fired when a violation has been encountered.
        /// </summary>
        public event EventHandler<ViolationEventArgs> ViolationEncountered;

        /// <summary>
        /// Event that is fired when a line of output is generated.
        /// </summary>
        public event EventHandler<OutputEventArgs> OutputGenerated;

        /// <summary>
        /// Event that is fired when one or more settings are changed on a project.
        /// </summary>
        public event EventHandler ProjectSettingsChanged;

        /// <summary>
        /// Event that is fired before the settings dialog is displayed, to allow
        /// listeners to add settings pages.
        /// </summary>
        public event EventHandler<AddSettingsPagesEventArgs> AddSettingsPages;

        #endregion Public Events

        #region Public Properties

        /// <summary>
        /// Gets a value indicating whether StyleCop is currently analying files.
        /// </summary>
        public bool Analyzing
        {
            get
            {
                return this.analyzing;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the currently running analysis should cancel.
        /// </summary>
        public bool Cancel
        {
            get
            {
                lock (this)
                {
                    return this.cancel;
                }
            }

            set
            {
                Param.Ignore(value);
                lock (this)
                {
                    this.cancel = value;
                }
            }
        }

        /// <summary>
        /// Gets the context for the currently executing analysis run, or null
        /// if no analysis is running.
        /// </summary>
        public RunContext RunContext
        {
            get
            {
                return this.runContext;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the results cache should be saved on disk.
        /// </summary>
        public bool WriteResultsCache
        {
            get
            {
                return this.writeResultsCache;
            }

            set
            {
                Param.Ignore(value);
                this.writeResultsCache = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether it is ok to display UI dialogs.
        /// </summary>
        public bool DisplayUI
        {
            get
            {
                return this.displayUI;
            }

            set
            {
                Param.Ignore(value);
                this.displayUI = value;
            }
        }

        /// <summary>
        /// Gets the collection of loaded code parsers.
        /// </summary>
        public ICollection<SourceParser> Parsers
        {
            get
            {
                return this.parsers.Values;
            }
        }

        /// <summary>
        /// Gets the environment that StyleCop is running under.
        /// </summary>
        public StyleCopEnvironment Environment
        {
            get
            {
                return this.environment;
            }
        }

        /// <summary>
        /// Gets the Registry object, which can be used for writing items to StyleCop's registry key.
        /// </summary>
        public RegistryUtils Registry
        {
            get
            {
                return this.registry;
            }
        }

        /// <summary>
        /// Gets the collection of global property descriptors exposed by StyleCop.
        /// </summary>
        public PropertyDescriptorCollection PropertyDescriptors
        {
            get
            {
                return this.coreParser.PropertyDescriptors;
            }
        }

        /// <summary>
        /// Gets the value of the optional tag filled in by the host at the time of this
        /// instance's creation.
        /// </summary>
        public object HostTag
        {
            get
            {
                return this.hostTag;
            }
        }

        #endregion Public Properties

        #region Internal Properties

        /// <summary>
        /// Gets the list of violations that can be triggered by the core module.
        /// </summary>
        internal CoreParser CoreViolations
        {
            get
            {
                return this.coreParser;
            }
        }

        #endregion Internal Properties

        #region Public Static Methods

        /// <summary>
        /// Loads the add-in resource xml at the given path.
        /// </summary>
        /// <param name="addInType">The add-in type.</param>
        /// <param name="resourceId">The resource ID of the analyzer xml.</param>
        /// <returns>Returns the loaded Xml or null if none was loaded.</returns>
        [SuppressMessage(
            "Microsoft.Usage", 
            "CA2202:Do not dispose objects multiple times", 
            Justification = "This is safe.")]
        [SuppressMessage(
            "Microsoft.Design", 
            "CA1059:MembersShouldNotExposeCertainConcreteTypes", 
            MessageId = "System.Xml.XmlNode", 
            Justification = "Compliance would break well-defined API.")]
        public static XmlDocument LoadAddInResourceXml(Type addInType, string resourceId)
        {
            Param.RequireNotNull(addInType, "addInType");
            Param.Ignore(resourceId);

            // If the add-in Xml Id was not specified, then use the default, which is the full name of the add-in
            // type plus ".xml"
            if (resourceId == null)
            {
                resourceId = SourceAnalyzer.GetIdFromAddInType(addInType) + ".xml";
            }

            // Check whether the resource exists.
            Log.Write(LogStrings.AttemptingToLoadAddInInitializationXmlFromPath, resourceId);
            if (addInType.Assembly.GetManifestResourceInfo(resourceId) == null)
            {
                Log.Write(LogStrings.CouldNotFildAddInInitializationXml);
                return null;
            }

            // Load the resource.
            using (Stream stream = addInType.Assembly.GetManifestResourceStream(resourceId))
            using (StreamReader reader = new StreamReader(stream))
            {
                string xml = reader.ReadToEnd();
                if (xml == null || xml.Length == 0)
                {
                    throw new ArgumentException(Strings.InvalidAddInXmlDocument);
                }

                XmlDocument analyzerXml = new XmlDocument();
                analyzerXml.LoadXml(xml);

                return analyzerXml;
            }
        }

        #endregion Public Static Methods

        #region Public Methods

        /// <summary>
        /// Disposes the contents of the class.
        /// </summary>
        public void Dispose()
        {
            if (this.log != null)
            {
                this.log.Dispose();
            }
        }

        /// <summary>
        /// Initializes the StyleCop core instance. This must be called before
        /// the object can be used.
        /// </summary>
        /// <param name="addInPaths">The list of paths to search under for parser and analyzer addins.
        /// Can be null if no addin paths are provided.</param>
        /// <param name="loadFromDefaultPath">Indicates whether to load addins
        /// from the default path, where the core binary is located.</param>
        public void Initialize(ICollection<string> addInPaths, bool loadFromDefaultPath)
        {
            Param.Ignore(addInPaths);
            Param.Ignore(loadFromDefaultPath);

            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            byte[] assemblyPublicKey = thisAssembly.GetName().GetPublicKeyToken();

            // Load analyzers from the default folder if necessary.
            if (loadFromDefaultPath)
            {
                this.LoadAddins(Path.GetDirectoryName(thisAssembly.Location), assemblyPublicKey);
            }

            // Now load all third-party addins found in other paths.
            if (addInPaths != null && addInPaths.Count > 0)
            {
                // Loop through each of the returned paths.
                foreach (string addinPath in addInPaths)
                {
                    string expandedAddinPath = System.Environment.ExpandEnvironmentVariables(addinPath);

                    // Make sure this path exists. If we get an exception trying
                    // to access the path, skip it.
                    try
                    {
                        if (!Directory.Exists(expandedAddinPath))
                        {
                            continue;
                        }
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (SecurityException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    // Load the addins from this path.
                    this.LoadAddins(expandedAddinPath, assemblyPublicKey);
                }
            }

            // Now go through each of the analyzers and add it to the appropriate parser.
            foreach (SourceAnalyzer analyzer in this.analyzers.Values)
            {
                SourceParser parser = this.parsers[analyzer.ParserId];
                if (parser != null)
                {
                    parser.Analyzers.Add(analyzer);
                    analyzer.SetParser(parser);
                }
            }
        }

        /// <summary>
        /// Analyzes the files within the given projects.
        /// </summary>
        /// <param name="projects">The list of code projects to analyze.</param>
        public void Analyze(IList<CodeProject> projects)
        {
            Param.RequireNotNull(projects, "projects");
            this.Analyze(projects, false, null, false, false);
        }

        /// <summary>
        /// Analyzes the files within the given projects.
        /// </summary>
        /// <param name="projects">The list of code projects to analyze.</param>
        /// <param name="settingsFilePath">The path to a StyleCop settings file to use during analysis.</param>
        public void Analyze(IList<CodeProject> projects, string settingsFilePath)
        {
            Param.RequireNotNull(projects, "projects");
            Param.RequireValidString(settingsFilePath, "settingsFilePath");

            this.Analyze(projects, false, settingsFilePath, false, false);
        }

        /// <summary>
        /// Analyzes the files within the given projects, ignoring all cached results.
        /// </summary>
        /// <param name="projects">The list of code projects to analyze.</param>
        public void FullAnalyze(IList<CodeProject> projects)
        {
            Param.RequireNotNull(projects, "projects");
            this.Analyze(projects, true, null, false, false);
        }

        /// <summary>
        /// Analyzes the files within the given projects, ignoring all cached results.
        /// </summary>
        /// <param name="projects">The list of code projects to analyze.</param>
        /// <param name="settingsFilePath">The path to a StyleCop settings file to use during analysis.</param>
        public void FullAnalyze(IList<CodeProject> projects, string settingsFilePath)
        {
            Param.RequireNotNull(projects, "projects");
            Param.RequireValidString(settingsFilePath, "settingsFilePath");

            this.Analyze(projects, true, settingsFilePath, false, false);
        }

        /// <summary>
        /// Auto-fixes the files within the given projects.
        /// </summary>
        /// <param name="projects">The list of code projects to auto-fix.</param>
        /// <param name="autoSave">Indicates whether to save the document back to the source.</param>
        public void AutoFix(IList<CodeProject> projects, bool autoSave)
        {
            Param.RequireNotNull(projects, "projects");
            Param.Ignore(autoSave);

            this.Analyze(projects, false, null, true, autoSave);
        }

        /// <summary>
        /// Auto-fixes the files within the given projects.
        /// </summary>
        /// <param name="projects">The list of code projects to auto-fix.</param>
        /// <param name="autoSave">Indicates whether to save the fixed document back to the source.</param>
        /// <param name="settingsFilePath">The path to a StyleCop settings file to use during auto-fixing.</param>
        public void AutoFix(IList<CodeProject> projects, bool autoSave, string settingsFilePath)
        {
            Param.RequireNotNull(projects, "projects");
            Param.Ignore(autoSave);
            Param.RequireValidString(settingsFilePath, "settingsFilePath");

            this.Analyze(projects, false, settingsFilePath, true, autoSave);
        }

        /// <summary>
        /// Displays the settings dialog for a project.
        /// </summary>
        /// <param name="settingsFilePath">The path to the settings file to edit.</param>
        /// <returns>Returns true if at least one settings change was made.</returns>
        public bool ShowSettings(string settingsFilePath)
        {
            Param.Ignore(settingsFilePath);
            return this.ShowSettings(settingsFilePath, StyleCopCore.ProjectSettingsPropertyPageIdProperty);
        }

        /// <summary>
        /// Displays the settings dialog for a project.
        /// </summary>
        /// <param name="settingsPath">The path to the settings to edit.</param>
        /// <param name="id">The ID of the settings property page.</param>
        /// <returns>Returns true if at least one settings change was made.</returns>
        public bool ShowSettings(string settingsPath, string id)
        {
            Param.RequireValidString(settingsPath, "settingsPath");
            Param.RequireValidString(id, "id");

            return this.ShowSettings(settingsPath, id, false);
        }

        /// <summary>
        /// Gets the analyzer with the given ID.
        /// </summary>
        /// <param name="analyzerId">The ID of the analyzer.</param>
        /// <returns>Returns the analyzer or null if there is no loaded analyzer with the given ID.</returns>
        public SourceAnalyzer GetAnalyzer(string analyzerId)
        {
            Param.RequireValidString(analyzerId, "analyzerId");

            SourceAnalyzer analyzer;
            if (this.analyzers.TryGetValue(analyzerId, out analyzer))
            {
                return analyzer;
            }

            return null;
        }

        /// <summary>
        /// Gets the parser with the given ID.
        /// </summary>
        /// <param name="parserId">The ID of the parser.</param>
        /// <returns>Returns the parser or null if there is no loaded parser with the given ID.</returns>
        public SourceParser GetParser(string parserId)
        {
            Param.RequireValidString(parserId, "parserId");

            SourceParser parser;
            if (this.parsers.TryGetValue(parserId, out parser))
            {
                return parser;
            }

            return null;
        }

        /// <summary>
        /// Gets the add-in with the given ID.
        /// </summary>
        /// <param name="addInId">The ID of the add-in.</param>
        /// <returns>Returns the add-in or null if there is no loaded add-in with the given ID.</returns>
        public StyleCopAddIn GetAddIn(string addInId)
        {
            Param.RequireValidString(addInId, "addInId");

            SourceParser parser;
            if (this.parsers.TryGetValue(addInId, out parser))
            {
                return parser;
            }

            SourceAnalyzer analyzer;
            if (this.analyzers.TryGetValue(addInId, out analyzer))
            {
                return analyzer;
            }

            return null;
        }

        #endregion Public Methods

        #region Internal Static Methods

        /// <summary>
        /// Gets the pages to display on the settings dialog.
        /// </summary>
        /// <param name="core">The StyleCop core instance.</param>
        /// <returns>Returns the list of settings pages to display.</returns>
        internal static List<IPropertyControlPage> GetSettingsPages(StyleCopCore core)
        {
            Param.AssertNotNull(core, "core");

            // Create an array of our property pages.
            List<IPropertyControlPage> pages = new List<IPropertyControlPage>();

            try
            {
                // Get the list of options pages from the addins.
                foreach (SourceParser parser in core.Parsers)
                {
                    // Load pages from this parser.
                    ICollection<IPropertyControlPage> parserPages = parser.SettingsPages;
                    if (parserPages != null && parserPages.Count > 0)
                    {
                        pages.AddRange(parserPages);
                    }

                    // Check each of the analyzers within this parser.
                    foreach (SourceAnalyzer analyzer in parser.Analyzers)
                    {
                        // Load pages from this analyzer.
                        ICollection<IPropertyControlPage> analyzerPages = analyzer.SettingsPages;
                        if (analyzerPages != null && analyzerPages.Count > 0)
                        {
                            pages.AddRange(analyzerPages);
                        }
                    }
                }

                return pages;
            }
            catch (Exception)
            {
                foreach (IPropertyControlPage page in pages)
                {
                    IDisposable disposable = page as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }

                pages.Clear();

                throw;
            }
        }

        /// <summary>
        /// Creates an absolute path given a relative path and the root directory.
        /// </summary>
        /// <param name="rootFolder">The root directory.</param>
        /// <param name="relativePath">The relative path.</param>
        /// <returns>Returns the absolute path.</returns>
        internal static string MakeAbsolutePath(string rootFolder, string relativePath)
        {
            Param.AssertValidString(rootFolder, "rootFolder");
            Param.AssertValidString(relativePath, "relativePath");

            // Make a copy of the root folder path.
            string absolutePath = rootFolder.Substring(0, rootFolder.Length);

            int index = 0;

            // Back up all directories specified in the relative path.
            while (true)
            {
                if (relativePath.Length - index < 3)
                {
                    break;
                }
                else if (relativePath[index] == '.' && relativePath[index + 1] == '\\')
                {
                    index += 2;
                }
                else if (relativePath[index] == '\\')
                {
                    index += 1;
                }
                else if (relativePath[index] == '.' && relativePath[index + 1] == '.' && relativePath[index + 2] == '\\')
                {
                    // Back up one folder.
                    index += 3;

                    // First, remove all backslashes from the end of the absolute path.
                    while (absolutePath.Length > 0 && absolutePath[absolutePath.Length - 1] == '\\')
                    {
                        absolutePath = absolutePath.Substring(0, absolutePath.Length - 1);
                    }

                    // Now cut off the last directory.
                    int lastSlashIndex = absolutePath.LastIndexOf("\\", StringComparison.Ordinal);
                    if (lastSlashIndex == -1)
                    {
                        // We've reached the end of the string. It's not possible to create 
                        // an absolute path.
                        return relativePath;
                    }

                    absolutePath = absolutePath.Substring(0, lastSlashIndex + 1);
                }
                else
                {
                    break;
                }
            }

            // Now add the remainder of the relative path onto the absolute path.
            return Path.Combine(absolutePath, relativePath.Substring(index, relativePath.Length - index));
        }

        /// <summary>
        /// Cleans up the given path so that it can always be matched against other paths.
        /// </summary>
        /// <param name="path">The path to clean.</param>
        /// <returns>Returns the cleaned path.</returns>
        internal static string CleanPath(string path)
        {
            Param.Ignore(path);

            string cleanedPath = path;
            if (cleanedPath != null)
            {
                // Remove backslashes from the end of the path.
                while (cleanedPath.Length > 0 && cleanedPath[cleanedPath.Length - 1] == '\\')
                {
                    cleanedPath = cleanedPath.Substring(0, cleanedPath.Length - 1);
                }

                cleanedPath = cleanedPath.ToUpperInvariant();
            }

            return cleanedPath;
        }

        #endregion Internal Static Methods

        #region Internal Methods

        /// <summary>
        /// Adds a generic violation.
        /// </summary>
        /// <param name="sourceCode">The file to add the violation to.</param>
        /// <param name="violation">The violation to add to the element.</param>
        internal void AddViolation(SourceCode sourceCode, Violation violation)
        {
            Param.Ignore(sourceCode, "sourceCode");
            Param.AssertNotNull(violation, "violation");

            bool signal = true;

            // Add the violation to the file.
            if (sourceCode != null)
            {
                if (!sourceCode.AddViolation(violation))
                {
                    signal = false;
                }
            }

            // Signal that there is a new violation.
            if (signal)
            {
                this.OnViolationEncountered(new ViolationEventArgs(violation));
            }
        }

        /// <summary>
        /// Adds a generic violation.
        /// </summary>
        /// <param name="element">The element to add the violation to.</param>
        /// <param name="violation">The violation to add to the element.</param>
        internal void AddViolation(ICodeElement element, Violation violation)
        {
            Param.Ignore(element, "element");
            Param.AssertNotNull(violation, "violation");

            // Add the violation to the element.
            if (element != null)
            {
                if (element.AddViolation(violation))
                {
                    this.OnViolationEncountered(new ViolationEventArgs(violation));
                }
            }
        }

        /// <summary>
        /// Adds a generic violation.
        /// </summary>
        /// <param name="element">The element to add the violation to.</param>
        /// <param name="type">The type of violation to add.</param>
        /// <param name="line">Line the violation appears on.</param>
        /// <param name="values">The string values to add to the context string.</param>
        internal void AddViolation(ICodeElement element, Rule type, int line, params object[] values)
        {
            Param.Ignore(element);
            Param.AssertNotNull(type, "type");
            Param.AssertGreaterThanZero(line, "line");
            Param.Ignore(values);

            // Build up the context string.
            StringBuilder message = new StringBuilder();
            message.AppendFormat(CultureInfo.CurrentCulture, type.Context, values);

            // Create the violation object and add it to the list.
            Violation violation = new Violation(type, element, line, message.ToString());

            // Finally, add the violation.
            this.AddViolation(element, violation);
        }

        /// <summary>
        /// Adds a generic violation.
        /// </summary>
        /// <param name="sourceCode">The source code document to add the violation to.</param>
        /// <param name="type">The type of violation to add.</param>
        /// <param name="line">Line the violation appears on.</param>
        /// <param name="values">The string values to add to the context string.</param>
        internal void AddViolation(SourceCode sourceCode, Rule type, int line, params object[] values)
        {
            Param.Ignore(sourceCode);
            Param.AssertNotNull(type, "type");
            Param.AssertGreaterThanZero(line, "line");
            Param.Ignore(values);

            // Build up the context string.
            StringBuilder message = new StringBuilder();
            message.AppendFormat(CultureInfo.CurrentCulture, type.Context, values);

            // Create the violation object and add it to the list.
            Violation violation = new Violation(type, sourceCode, line, message.ToString());

            // Finally, add the violation.
            this.AddViolation(sourceCode, violation);
        }

        /// <summary>
        /// Fires an output generated event.
        /// </summary>
        /// <param name="output">The output to display.</param>
        internal void SignalOutput(string output)
        {
            Param.AssertNotNull(output, "output");

            this.SignalOutput(MessageImportance.Normal, output);
        }

        /// <summary>
        /// Fires an output generated event.
        /// </summary>
        /// <param name="importance">Level of importance for the output.</param>
        /// <param name="output">The output to display.</param>
        internal void SignalOutput(MessageImportance importance, string output)
        {
            Param.AssertNotNull(output, "output");
            Param.Ignore(importance);

            this.OnOutputGenerated(new OutputEventArgs(output, importance));
        }

        /// <summary>
        /// Displays the settings dialog for a project.
        /// </summary>
        /// <param name="settingsPath">The path to the settings to edit.</param>
        /// <param name="id">The ID of the settings property page.</param>
        /// <param name="defaultSettings">The settings being shown are the default settings for the installation.</param>
        /// <returns>Returns true if at least one settings change was made.</returns>
        internal bool ShowSettings(string settingsPath, string id, bool defaultSettings)
        {
            Param.AssertValidString(settingsPath, "settingsPath");
            Param.AssertValidString(id, "id");
            Param.Ignore(defaultSettings);

            // Get the list of settings pages from each of the analyzers and parsers.
            List<IPropertyControlPage> pages = StyleCopCore.GetSettingsPages(this);

            try
            {
                // Add the analyzer options page at the beginning.
                pages.Insert(0, new AnalyzersOptions());

                // And insert the settings tab after that.
                pages.Insert(1, new GlobalSettingsFileOptions());

                // And the cache tab after that.
                pages.Insert(2, new CacheOptions());

                // Get settings pages from event listeners and add them to the end.
                AddSettingsPagesEventArgs eventArgs = new AddSettingsPagesEventArgs(settingsPath);
                this.OnAddSettingsPages(eventArgs);

                foreach (IPropertyControlPage pageFromEvent in eventArgs.Pages)
                {
                    pages.Add(pageFromEvent);
                }

                // Set the appropriate dialog title.
                string title = defaultSettings ? Strings.DefaultSettingsDialogTitle : Strings.LocalSettingsDialogTitle;

                // Display the project settings dialog.
                return this.ShowProjectSettings(settingsPath, pages.AsReadOnly(), title, id, defaultSettings);
            }
            finally
            {
                if (pages != null)
                {
                    foreach (IPropertyControlPage page in pages)
                    {
                        IDisposable disposable = page as IDisposable;
                        disposable.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Displays a settings dialog for a project.
        /// </summary>
        /// <param name="settingsPath">The path to the settings.</param>
        /// <param name="pages">The list of pages to display on the settings dialog.</param>
        /// <param name="caption">The caption for the dialog.</param>
        /// <param name="id">A unique identifier string for the property page group.</param>
        /// <param name="defaultSettings">Indicates whether these are the default settings for the installation.</param>
        /// <returns>Returns true if at least one settings change was made.</returns>
        internal bool ShowProjectSettings(
            string settingsPath,
            IList<IPropertyControlPage> pages,
            string caption,
            string id,
            bool defaultSettings)
        {
            Param.AssertValidString(settingsPath, "settingsPath");
            Param.AssertNotNull(pages, "pages");
            Param.AssertValidString(caption, "caption");
            Param.AssertValidString(id, "id");
            Param.Ignore(defaultSettings);

            // Load the local settings.
            Exception exception = null;
            WritableSettings localSettings = this.environment.GetWritableSettings(settingsPath, out exception);

            if (exception != null)
            {
                AlertDialog.Show(
                    this,
                    null,
                    string.Format(CultureInfo.CurrentUICulture, Strings.ProjectSettingsFileNotLoadedOrCreated, exception.Message),
                    Strings.Title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            else if (localSettings != null)
            {
                localSettings.DefaultSettings = defaultSettings;
                return this.ShowProjectSettings(localSettings, pages, caption, id);
            }

            return false;
        }

        /// <summary>
        /// Displays a settings dialog for a project.
        /// </summary>
        /// <param name="settings">The settings manager.</param>
        /// <param name="pages">The list of pages to display on the settings dialog.</param>
        /// <param name="caption">The caption for the dialog.</param>
        /// <param name="id">A unique identifier string for the property page group.</param>
        /// <returns>Returns true if at least one settings change was made.</returns>
        internal bool ShowProjectSettings(
            WritableSettings settings,
            IList<IPropertyControlPage> pages,
            string caption,
            string id)
        {
            Param.AssertNotNull(settings, "settings");
            Param.AssertNotNull(pages, "pages");
            Param.AssertValidString(caption, "caption");
            Param.AssertValidString(id, "id");

            // Create the properties dialog object.
            using (PropertyDialog properties = new PropertyDialog(pages, settings, id, this, null))
            {
                // Set the dialog title.
                properties.Text = caption;

                // Make sure that we're not running in a non-UI mode.
                if (!this.displayUI)
                {
                    throw new InvalidOperationException(Strings.CannotDisplaySettingsInNonUIMode);
                }

                // Show the dialog.
                properties.ShowDialog();

                // Always fire the event regardless of whether any settings were changed. This ensures
                // that everything is always updated properly when settings change.
                this.OnProjectSettingsChanged(new EventArgs());

                // Return true if one or more properties were changed.
                return properties.SettingsChanged;
            }
        }

        #endregion Internal Methods

        #region Private Static Methods

        #if !DEBUGTHREADING
        /// <summary>
        /// Gets the number of CPUs on the machine.
        /// </summary>
        /// <returns>The number of CPUs on the machine.</returns>
        private static int GetCpuCount()
        {
            int count = 1;

            RegistryKey key = Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor", false);
            if (key != null)
            {
                if (key.SubKeyCount >= 1)
                {
                    count = key.SubKeyCount;
                }

                key.Close();
            }

            return count;
        }
        #endif

        /// <summary>
        /// Compares the given configuration with the given list of flags.
        /// </summary>
        /// <param name="configuration">The configuration object.</param>
        /// <param name="flagList">The list of flags.</param>
        /// <returns>Returns true if the configuration is identical to the flag list, or
        /// false otherwise.</returns>
        private static bool CompareCachedConfiguration(Configuration configuration, string flagList)
        {
            Param.AssertNotNull(configuration, "configuration");
            Param.AssertNotNull(flagList, "flagList");

            // Split the flags.
            string[] flags = new string[0];
            string trimmedList = flagList.Trim();
            if (trimmedList.Length > 0)
            {
                flags = flagList.Split(';');
            }

            // If the counts are different, the configurations are different.
            if (flags.Length != configuration.Flags.Count)
            {
                return false;
            }

            // Make sure each of the flags exists in the configuration.
            foreach (string flag in flags)
            {
                if (!configuration.Contains(flag))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Initializes the project to prepare it for analysis.
        /// </summary>
        /// <param name="project">The project to initialize.</param>
        /// <param name="data">The analysis data object.</param>
        /// <param name="cache">The file cache.</param>
        private static void InitializeProjectForAnalysis(CodeProject project, StyleCopThread.Data data, ResultsCache cache)
        {
            Param.AssertNotNull(project, "project");
            Param.AssertNotNull(data, "data");
            Param.Ignore(cache);

            // Get the status object for the project.
            ProjectStatus projectStatus = data.GetProjectStatus(project);
            Debug.Assert(projectStatus != null, "There is no status for the given project.");

            // Load the settings for the project. If the project already contains settings, use those. 
            // Otherwise, load them from scratch.
            if (!project.SettingsLoaded)
            {
                project.Settings = data.GetSettings(project);
                project.SettingsLoaded = true;
            }

            // Load the project configuration from the cache and compare it to the
            // current project configuration.
            string configuration = cache == null ? null : cache.LoadProject(project);
            if (configuration == null)
            {
                projectStatus.IgnoreResultsCache = true;
            }
            else
            {
                projectStatus.IgnoreResultsCache = !StyleCopCore.CompareCachedConfiguration(
                    project.Configuration, configuration);
            }

            if (cache != null && project.WriteCache)
            {
                cache.SaveProject(project);
            }
        }

        /// <summary>
        /// Gets the next type in the given add-in type's inheritence chain that implements the given attribute.
        /// </summary>
        /// <param name="addInType">The add-in type.</param>
        /// <param name="attributeType">The attribute type.</param>
        /// <param name="attribute">Returns the attribute, if found.</param>
        /// <returns>Returns the next type, or null if there are no types containing the attribute.</returns>
        private static Type GetNextAddInAttributeType(Type addInType, Type attributeType, out object attribute)
        {
            Param.Ignore(addInType);
            Param.AssertNotNull(attributeType, "attributeType");

            attribute = null;

            while (addInType != null)
            {
                object[] attributes = addInType.GetCustomAttributes(attributeType, false);
                if (attributes != null && attributes.Length > 0)
                {
                    attribute = attributes[0];
                    return addInType;
                }

                addInType = addInType.BaseType;
            }

            return addInType;
        }

        /// <summary>
        /// Compares two public keys to see if they are the same.
        /// </summary>
        /// <param name="key1">The first public key.</param>
        /// <param name="key2">The second public key.</param>
        /// <returns>true if the keys are the same; false otherwise.</returns>
        private static bool ComparePublicKeys(byte[] key1, byte[] key2)
        {
            Param.AssertNotNull(key1, "key1");
            Param.AssertNotNull(key2, "key2");

            if (key1.Length != key2.Length)
            {
                return false;
            }

            for (int i = 0; i < key1.Length; ++i)
            {
                if (key1[i] != key2[i])
                {
                    return false;
                }
            }

            return true;
        }

        #endregion Private Static Methods

        #region Private Methods

        /// <summary>
        /// Called when a violation is encountered while analyzing a code document.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnViolationEncountered(ViolationEventArgs e)
        {
            Param.AssertNotNull(e, "e");

            if (this.ViolationEncountered != null)
            {
                this.ViolationEncountered(this, e);
            }
        }

        /// <summary>
        /// Called when a line of output is generated while analyzing a code document.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnOutputGenerated(OutputEventArgs e)
        {
            Param.AssertNotNull(e, "e");

            if (this.OutputGenerated != null)
            {
                this.OutputGenerated(this, e);
            }
        }

        /// <summary>
        /// Called when the settings are changed for one or more projects.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnProjectSettingsChanged(EventArgs e)
        {
            Param.AssertNotNull(e, "e");

            if (this.ProjectSettingsChanged != null)
            {
                this.ProjectSettingsChanged(this, e);
            }
        }

        /// <summary>
        /// Event that is fired before the settings dialog is displayed, to allow
        /// listeners to add settings pages.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnAddSettingsPages(AddSettingsPagesEventArgs e)
        {
            Param.AssertNotNull(e, "e");

            if (this.AddSettingsPages != null)
            {
                this.AddSettingsPages(this, e);
            }
        }

        /// <summary>
        /// Loads the addin modules.
        /// </summary>
        /// <param name="path">The path to the addin modules.</param>
        /// <param name="publicKey">The public key of the core assembly.</param>
        [SuppressMessage(
            "Microsoft.Reliability", 
            "CA2001:AvoidCallingProblematicMethods", 
            MessageId = "System.Reflection.Assembly.LoadFrom",
            Justification = "No alternative is provided.")]
        private void LoadAddins(string path, byte[] publicKey)
        {
            Param.AssertNotNull(path, "path");
            Param.AssertValidCollection(publicKey, "publicKey");

            try
            {
                if (Directory.Exists(path))
                {
                    Log.Write(LogStrings.LoadAddInsFromPath, path);

                    // Find all DLL assemblies in the path and loop through them.
                    string[] assemblyPaths = Directory.GetFiles(path, "*.dll");
                    foreach (string assemblyPath in assemblyPaths)
                    {
                        // We want to skip the StyleCop assemblies.
                        if (!assemblyPath.EndsWith("\\StyleCop.dll", StringComparison.OrdinalIgnoreCase) &&
                            !assemblyPath.EndsWith("\\StyleCop.vspackage.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                Log.Write(LogStrings.LoadingAssembly, assemblyPath);
                                Assembly assembly = Assembly.LoadFrom(assemblyPath);

                                // BUGBUG: For some reason, GetExportedTypes throws a FileNotFoundException
                                // while loading addins that reference StyleCop.dll (this assembly). 
                                // This exception is NOT thrown as long as I call GetCustomAttributes on
                                // the assembly before calling GetExportedTypes. I have no idea why this is.
                                assembly.GetCustomAttributes(true);

                                // Now get the list of exported types from the assembly.
                                Type[] types = assembly.GetExportedTypes();

                                // Match the assembly's public key.
                                byte[] addInAssemblyPublicKey = assembly.GetName().GetPublicKeyToken();
                                bool keyMatch = ComparePublicKeys(publicKey, addInAssemblyPublicKey);

                                foreach (Type type in types)
                                {
                                    if (type.IsSubclassOf(typeof(SourceAnalyzer)))
                                    {
                                        Log.Write(LogStrings.DiscoveredSourceAnalyzerType, type.Name);

                                        SourceAnalyzer analyzer = this.InitializeAddIn(type, keyMatch) as SourceAnalyzer;

                                        if (analyzer != null && !this.analyzers.ContainsKey(analyzer.Id))
                                        {
                                            this.analyzers.Add(analyzer.Id, analyzer);
                                        }
                                    }
                                    else if (type.IsSubclassOf(typeof(SourceParser)))
                                    {
                                        Log.Write(LogStrings.DiscoveredSourceParserType, type.Name);

                                        SourceParser parser = this.InitializeAddIn(type, keyMatch) as SourceParser;
                                        if (parser != null && !this.parsers.ContainsKey(parser.Id))
                                        {
                                            this.parsers.Add(parser.Id, parser);

                                            // Let the environment know about the parser.
                                            this.environment.AddParser(parser);
                                        }
                                    }
                                }
                            }
                            catch (BadImageFormatException)
                            {
                                // Attempting to load certain dll's (corrupted or native) may throw a BadImageFormatException.
                                // We do not consider it a failure if we cannot load add-ins from these assemblies.
                            }
                            catch (ThreadAbortException)
                            {
                                // The thread is being aborted. Stop loading the Add-ins.
                            }
                            catch (OutOfMemoryException)
                            {
                                // If we run out of memory, we cannot load and complete analysis anyway.
                                throw;
                            }
                            catch (Exception ex)
                            {
                                AlertDialog.Show(
                                    this,
                                    null,
                                    string.Format(CultureInfo.CurrentUICulture, Strings.ExceptionWhileLoadingAddins, ex.GetType(), ex.Message),
                                    Strings.Title,
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);

                                throw;
                            }
                        }
                    }

                    string[] directories = Directory.GetDirectories(path, "*.*");
                    foreach (string directory in directories)
                    {
                        this.LoadAddins(directory, publicKey);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SecurityException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Instantiates an add-in object from the given assembly, with the given class name.
        /// </summary>
        /// <param name="addInType">The type of the add-in.</param>
        /// <param name="isKnownAssembly">Indicates whether the add-in comes from a known assembly.</param>
        /// <returns>Returns the created, initialized add-in.</returns>
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "This is a false violation.")]
        private StyleCopAddIn InitializeAddIn(Type addInType, bool isKnownAssembly)
        {
            Param.AssertNotNull(addInType, "addInType");
            Param.Ignore(isKnownAssembly);

            // Get the add-in attribute from the type, if it exists.
            object attributeObject;
            Type typeContainingAttribute = GetNextAddInAttributeType(
                addInType, typeof(StyleCopAddInAttribute), out attributeObject);
            if (typeContainingAttribute == null)
            {
                // This is not a valid add-in type.
                Log.Write(LogStrings.TheTypeDoesNotContainTheStyleCopAddInAttribute, addInType.Name);
                return null;
            }

            // Create the add-in instance.
            Log.Write(LogStrings.CreatingAnInstanceOfType, addInType.Name);
            StyleCopAddIn addInObject = (StyleCopAddIn)Activator.CreateInstance(addInType);

            // Load the add-in xml.
            StyleCopAddInAttribute attribute = (StyleCopAddInAttribute)attributeObject;
            XmlDocument addInXml = LoadAddInResourceXml(addInType, attribute.AddInXmlId);
            if (addInXml == null)
            {
                // The initialization xml was not valid.
                Log.Write(LogStrings.FailedToLoadAddInInitializationXml);
                return null;
            }

            // Initialize the add-in.
            addInObject.Initialize(this, addInXml, true, isKnownAssembly);

            // Now load any other initialization xml from parent types.
            while (true)
            {
                typeContainingAttribute = GetNextAddInAttributeType(
                    typeContainingAttribute.BaseType, typeof(StyleCopAddInAttribute), out attributeObject);

                if (typeContainingAttribute == null)
                {
                    break;
                }

                attribute = (StyleCopAddInAttribute)attributeObject;
                addInXml = LoadAddInResourceXml(typeContainingAttribute, attribute.AddInXmlId);
                if (addInXml != null)
                {
                    addInObject.Initialize(this, addInXml, false, isKnownAssembly);
                }
            }

            // Allow the add-in inheritors to initialize themselves.
            addInObject.InitializeAddIn();

            // Add a default "Enabled" property descriptor for every rule exposed by this add-in.
            foreach (Rule rule in addInObject.AddInRules)
            {
                PropertyDescriptor<bool> ruleEnabledPropertyDescriptor = new PropertyDescriptor<bool>(
                    rule.Name + "#Enabled", PropertyType.Boolean, string.Empty, string.Empty, true, false, rule.EnabledByDefault);
                addInObject.PropertyDescriptors.AddPropertyDescriptor(ruleEnabledPropertyDescriptor);
            }

            return addInObject;
        }

        /// <summary>
        /// Performs analysis of the given projects.
        /// </summary>
        /// <param name="projects">The list of code projects to analyze.</param>
        /// <param name="ignoreCache">True if the cache files should be ignored.</param>
        /// <param name="settingsPath">The path to the settings to use during analysis.</param>
        /// <param name="autoFix">Indicates whether to run auto-fix rather than analysis.</param>
        /// <param name="autoSave">If autoFix is true, this flag indicates whether to auto-save the document back to the source.</param>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Cannot allow exception from plug-in to kill VS or build")]
        private void Analyze(IList<CodeProject> projects, bool ignoreCache, string settingsPath, bool autoFix, bool autoSave)
        {
            Param.AssertNotNull(projects, "projects");
            Param.Ignore(ignoreCache);
            Param.Ignore(settingsPath);
            Param.Ignore(autoFix);
            Param.Ignore(autoSave);

            // Indicate that we're analyzing.
            lock (this)
            {
                this.analyzing = true;
                this.cancel = false;
                this.runContext = new RunContext(autoFix);
            }

            // Get the CPU count.
            #if DEBUGTHREADING
            // For debugging, only create a single worker thread.
            int threadCount = 1;
            #else
            // Create a maximum of two worker threads.
            int threadCount = Math.Max(GetCpuCount(), 2);
            #endif

            try
            {
                // Intialize each of the parsers.
                foreach (SourceParser parser in this.parsers.Values)
                {
                    parser.PreParse();

                    // Initialize each of the enabled rules dictionaries for the analyzers.
                    foreach (SourceAnalyzer analyzer in parser.Analyzers)
                    {
                        analyzer.PreAnalyze();
                    }
                }

                // Reads and writes the results cache.
                ResultsCache resultsCache = null;

                if (this.writeResultsCache)
                {
                    resultsCache = new ResultsCache(this);
                }

                // Create a data object which will passed to each worker.
                StyleCopThread.Data data = new StyleCopThread.Data(
                    this, projects, resultsCache, this.runContext, autoSave, ignoreCache || autoFix, settingsPath);

                // Initialize each of the projects before analysis.
                foreach (CodeProject project in projects)
                {
                    StyleCopCore.InitializeProjectForAnalysis(project, data, resultsCache);
                }

                // Run until each of the parsers have completely finished analyzing all of the files.
                while (!this.Cancel)
                {
                    // Reset the file enumeration index.
                    data.ResetEmumerator();

                    // Run the worker threads and wait for them to complete.
                    if (this.RunWorkerThreads(data, threadCount))
                    {
                        // Analysis of all files has been completed.
                        break;
                    }

                    // Increment the pass number for the next round.
                    ++data.PassNumber;
                }

                // Save the cache files back to the disk.
                if (resultsCache != null)
                {
                    resultsCache.Flush();
                }

                // Finalize each of the parsers.
                foreach (SourceParser parser in this.parsers.Values)
                {
                    parser.PostParse();
                }

                // Clear the enabled rules lists from all analyzers since they are no longer needed.
                foreach (SourceParser parser in this.Parsers)
                {
                    foreach (SourceAnalyzer analyzer in parser.Analyzers)
                    {
                        analyzer.PostAnalyze();
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                // Don't log OutOfMemoryExceptions since there is no memory!
                throw;
            }
            catch (ThreadAbortException)
            {
                // The thread is being aborted. Stop analyzing the source files.
            }
            catch (Exception ex)
            {
                // We catch all exceptions here so that we can log a violation. 
                Debug.Assert(false, "Unhandled exception while analyzing files: " + ex.Message);
                this.coreParser.AddViolation(null, 1, Rules.ExceptionOccurred, ex.GetType(), ex.Message);

                // Do not re-throw the exception as this can crash Visual Studio or the build system that StyleCop is running under.
            }
            finally
            {
                // Indicate that we're done analyzing.
                lock (this)
                {
                    this.analyzing = false;
                    this.runContext = null;
                }
            }
        }

        /// <summary>
        /// Launches the worker threads and waits for them to complete.
        /// </summary>
        /// <param name="data">The threading data.</param>
        /// <param name="count">The number of threads to create.</param>
        /// <returns>Returns a value indicating whether analysis of all
        /// files has been completed. If this returns false, another round
        /// of analysis must be performed.</returns>
        private bool RunWorkerThreads(StyleCopThread.Data data, int count)
        {
            Param.AssertNotNull(data, "data");
            Param.AssertGreaterThanZero(count, "count");

            // Indicates whether total sanalysis of all files has been completed.
            bool complete = true;

            // Create the worker and thread class arrays.
            BackgroundWorker[] workers = new BackgroundWorker[count];
            StyleCopThread[] threadClasses = new StyleCopThread[count];

            // Allocate and start all the threads.
            for (int i = 0; i < count; ++i)
            {
                // Allocate the worker classes for this thread.
                workers[i] = new BackgroundWorker();
                threadClasses[i] = new StyleCopThread(data);

                // Register for events on the background worker class.
                workers[i].DoWork += new DoWorkEventHandler(threadClasses[i].DoWork);

                // Register for the completion event on the thread data class. We do not use the standard BackgroundWorker
                // completion event because for some reason it does not get fired when running inside of Visual Studio using
                // the MSBuild task, and so everything ends up blocked. This may have to do with the way Visual Studio uses 
                // threads when running a build. Therefore, we do not rely on the BackgroundWorker's completion event, and
                // instead use our own event.
                threadClasses[i].ThreadCompleted += new EventHandler<StyleCopThreadCompletedEventArgs>(this.StyleCopThreadCompleted);

                // Indicate that we are launching another thread.
                data.IncrementThreadCount();
            }

            // The lock is required so that we can wait on the Monitor.
            lock (this)
            {
                // Start each of the worker threads.
                for (int i = 0; i < count; ++i)
                {
                    workers[i].RunWorkerAsync();
                }

                // Wait for the threads to complete.
                Monitor.Wait(this);
            }

            // Dispose the workers and determine whether all analysis is complete.
            for (int i = 0; i < count; ++i)
            {
                workers[i].Dispose();

                if (!threadClasses[i].Complete)
                {
                    complete = false;
                }
            }

            return complete;
        }

        /// <summary>
        /// Called when one of the worker threads completes its work.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void StyleCopThreadCompleted(object sender, StyleCopThreadCompletedEventArgs e)
        {
            Param.Ignore(sender);
            Param.AssertNotNull(e, "e");

            // Get the data object.
            Debug.Assert(e.Data != null, "The thread data object should not be null.");

            lock (this)
            {
                // Decrement the thread count.
                if (e.Data.DecrementThreadCount() == 0)
                {
                    // Release the master thread, which is currently waiting for all the workers to complete.
                    Monitor.Pulse(this);
                }
            }
        }

        #endregion Private Methods
    }
}
