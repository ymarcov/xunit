﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit.Internal;
using Xunit.Runner.Common;

namespace Xunit.Runner.InProc.SystemConsole
{
	/// <summary>
	/// The command line parser for the console runner.
	/// </summary>
	public class CommandLine
	{
		readonly Stack<string> arguments = new();
		XunitProject? project;
		readonly List<string> unknownOptions = new();

		/// <summary>
		/// Initializes a new instance of the <see cref="CommandLine"/> class.
		/// </summary>
		/// <param name="assembly">The assembly under test.</param>
		/// <param name="assemblyFileName">The optional assembly filename.</param>
		/// <param name="args">The command line arguments passed to the Main method.</param>
		/// <param name="fileExists">An optional delegate which checks for file existence.
		/// Available as an override solely for testing purposes.</param>
		protected CommandLine(
			Assembly assembly,
			string? assemblyFileName,
			string[] args,
			Predicate<string>? fileExists = null)
		{
			try
			{
				fileExists ??= File.Exists;

				for (var i = args.Length - 1; i >= 0; i--)
					arguments.Push(args[i]);

				Project = Parse(assembly, assemblyFileName, fileExists);
			}
			catch (Exception ex)
			{
				ParseFault = ex;
			}
		}

		/// <summary>
		/// Gets the fault that happened during parsing.
		/// </summary>
		public Exception? ParseFault { get; protected set; }

		/// <summary>
		/// Gets or sets the project that describes the assembly to be tested.
		/// </summary>
		public XunitProject Project
		{
			get => project ?? throw new InvalidOperationException($"Attempted to get {nameof(Project)} on an uninitialized '{GetType().FullName}' object");
			protected set => project = Guard.ArgumentNotNull(nameof(Project), value);
		}

		/// <summary>
		/// <para>Option: -tcp</para>
		/// <para>When set, indicates that the runner should connected to the given TCP
		/// port and communicate using the v3 runner protocol. Valid values are integers
		/// between 1024 and 65535.</para>
		/// </summary>
		public int? TcpPort { get; protected set; }

		/// <summary>
		/// Chooses a reporter from the list of available reporters. Unless
		/// <see cref="Project"/>.<see cref="XunitProject.Configuration"/>.<see cref="TestProjectConfiguration.NoAutoReporters"/>
		/// is set to <c>true</c>, it will first look for an environmentally enabled reporter;
		/// if none is available, then it will search through the command line options to
		/// determine which one to run. If there are no environmentally enabled reporters and
		/// no reporters passed on the command line, it will return an instance of
		/// <see cref="DefaultRunnerReporter"/>.
		/// </summary>
		/// <param name="reporters">The list of available reporters to choose from</param>
		/// <returns>The reporter that should be used during testing</returns>
		public IRunnerReporter ChooseReporter(IReadOnlyList<IRunnerReporter> reporters)
		{
			var result = default(IRunnerReporter);

			foreach (var unknownOption in unknownOptions)
			{
				var reporter = reporters.FirstOrDefault(r => r.RunnerSwitch == unknownOption) ?? throw new ArgumentException($"unknown option: -{unknownOption}");

				if (TcpPort.HasValue)
					throw new ArgumentException($"cannot specify -{reporter.RunnerSwitch} when using -tcp");

				if (result != null)
					throw new ArgumentException("only one reporter is allowed");

				result = reporter;
			}

			if (!Project.Configuration.NoAutoReportersOrDefault)
				result = reporters.FirstOrDefault(r => r.IsEnvironmentallyEnabled) ?? result;

			return result ?? new DefaultRunnerReporter();
		}

		/// <summary>
		/// For testing purposes only. Do not use.
		/// </summary>
		protected virtual string GetFullPath(string fileName) =>
			Path.GetFullPath(fileName);

		XunitProject GetProjectFile(
			Assembly assembly,
			string? assemblyFileName,
			string? configFileName)
		{
			var project = new XunitProject();
			var targetFramework = assembly.GetTargetFramework();
			var projectAssembly = new XunitProjectAssembly(project)
			{
				Assembly = assembly,
				AssemblyFilename = assemblyFileName,
				ConfigFilename = configFileName != null ? GetFullPath(configFileName) : null,
				TargetFramework = targetFramework
			};

			ConfigReader_Json.Load(projectAssembly.Configuration, projectAssembly.AssemblyFilename, projectAssembly.ConfigFilename);

			project.Add(projectAssembly);
			return project;
		}

		static void GuardNoOptionValue(KeyValuePair<string, string?> option)
		{
			if (option.Value != null)
				throw new ArgumentException($"error: unknown command line option: {option.Value}");
		}

		static bool IsConfigFile(string fileName) =>
			fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Parses the command line, and returns an instance of <see cref="CommandLine"/> that
		/// has been populated based on the command line options that were passed.
		/// </summary>
		/// <param name="assembly">The assembly under test.</param>
		/// <param name="assemblyFileName">The optional assembly filename.</param>
		/// <param name="args">The command line arguments passed to the Main method.</param>
		/// <returns>The instance of the <see cref="CommandLine"/> object.</returns>
		public static CommandLine Parse(
			Assembly assembly,
			string? assemblyFileName,
			params string[] args) =>
				new(assembly, assemblyFileName, args);

		/// <summary>
		/// For testing purposes only. Do not use.
		/// </summary>
		protected XunitProject Parse(
			Assembly assembly,
			string? assemblyFileName,
			Predicate<string> fileExists)
		{
			var configFileName = default(string);

			if (arguments.Count > 0 && !arguments.Peek().StartsWith("-", StringComparison.Ordinal))
			{
				configFileName = arguments.Pop();
				if (!IsConfigFile(configFileName))
					throw new ArgumentException($"expecting config file, got: {configFileName}");
				if (!fileExists(configFileName))
					throw new ArgumentException($"config file not found: {configFileName}");
			}

			var project = GetProjectFile(assembly, assemblyFileName, configFileName);

			while (arguments.Count > 0)
			{
				var option = PopOption(arguments);
				var optionName = option.Key.ToLowerInvariant();

				if (!optionName.StartsWith("-", StringComparison.Ordinal))
					throw new ArgumentException($"expected option, instead got: {option.Key}");

				optionName = optionName.Substring(1);

				if (optionName == "class")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -class");

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.IncludedClasses.Add(option.Value);
				}
				else if (optionName == "culture")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -culture");

					var culture = option.Value switch
					{
						"default" => null,
						"invariant" => "",
						_ => option.Value
					};

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Culture = culture;
				}
				else if (optionName == "debug")
				{
					GuardNoOptionValue(option);
					project.Configuration.Debug = true;
				}
				else if (optionName == "diagnostics")
				{
					GuardNoOptionValue(option);
					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.DiagnosticMessages = true;
				}
				else if (optionName == "failskips")
				{
					GuardNoOptionValue(option);
					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.FailSkips = true;
				}
				else if (optionName == "ignorefailures")
				{
					GuardNoOptionValue(option);
					project.Configuration.IgnoreFailures = true;
				}
				else if (optionName == "internaldiagnostics")
				{
					GuardNoOptionValue(option);
					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.InternalDiagnosticMessages = true;
				}
				else if (optionName == "list")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -list");

					var pieces = option.Value.Split('/');
					var list = default((ListOption Option, ListFormat Format)?);

					if (pieces.Length < 3 && Enum.TryParse<ListOption>(pieces[0], ignoreCase: true, out var listOption))
					{
						if (pieces.Length == 1)
							list = (listOption, ListFormat.Text);
						else if (Enum.TryParse<ListFormat>(pieces[1], ignoreCase: true, out var listFormat))
							list = (listOption, listFormat);
					}

					project.Configuration.List = list ?? throw new ArgumentException("invalid argument for -list");
					project.Configuration.NoLogo = true;
				}

				else if (optionName == "maxthreads")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -maxthreads");

					int? maxParallelThreads = null;

					switch (option.Value)
					{
						case "0":
						case "default":
							break;

						// Can't support "-1" here because it's interpreted as a new command line switch
						case "unlimited":
							maxParallelThreads = -1;
							break;

						default:
							var match = ConfigUtility.MultiplierStyleMaxParallelThreadsRegex.Match(option.Value);
							if (match.Success && decimal.TryParse(match.Groups[1].Value, out var maxThreadMultiplier))
								maxParallelThreads = (int)(maxThreadMultiplier * Environment.ProcessorCount);
							else if (int.TryParse(option.Value, out var threadValue) && threadValue > 0)
								maxParallelThreads = threadValue;
							else
								throw new ArgumentException("incorrect argument value for -maxthreads (must be 'default', 'unlimited', a positive number, or a multiplier in the form of '0.0x')");

							break;
					}

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.MaxParallelThreads = maxParallelThreads;
				}
				else if (optionName == "method")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -method");

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.IncludedMethods.Add(option.Value);
				}
				else if (optionName == "namespace")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -namespace");

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.IncludedNamespaces.Add(option.Value);
				}
				else if (optionName == "noautoreporters")
				{
					GuardNoOptionValue(option);
					project.Configuration.NoAutoReporters = true;
				}
				else if (optionName == "noclass")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -noclass");

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.ExcludedClasses.Add(option.Value);
				}
				else if (optionName == "nocolor")
				{
					GuardNoOptionValue(option);
					project.Configuration.NoColor = true;
				}
				else if (optionName == "nologo")
				{
					GuardNoOptionValue(option);
					project.Configuration.NoLogo = true;
				}
				else if (optionName == "nomethod")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -nomethod");

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.ExcludedMethods.Add(option.Value);
				}
				else if (optionName == "nonamespace")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -nonamespace");

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.ExcludedNamespaces.Add(option.Value);
				}
				else if (optionName == "notrait")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -notrait");

					var pieces = option.Value.Split('=');
					if (pieces.Length != 2 || string.IsNullOrEmpty(pieces[0]) || string.IsNullOrEmpty(pieces[1]))
						throw new ArgumentException("incorrect argument format for -notrait (should be \"name=value\")");

					var name = pieces[0];
					var value = pieces[1];

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.ExcludedTraits.Add(name, value);
				}
				else if (optionName == "parallel")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -parallel");

					if (!Enum.TryParse(option.Value, ignoreCase: true, out ParallelismOption parallelismOption))
						throw new ArgumentException("incorrect argument value for -parallel");

					var (parallelizeAssemblies, parallelizeTestCollections) = parallelismOption switch
					{
						ParallelismOption.all => (true, true),
						ParallelismOption.assemblies => (true, false),
						ParallelismOption.collections => (false, true),
						_ => (false, false)
					};

					foreach (var projectAssembly in project.Assemblies)
					{
						projectAssembly.Configuration.ParallelizeAssembly = parallelizeAssemblies;
						projectAssembly.Configuration.ParallelizeTestCollections = parallelizeTestCollections;
					}
				}
				else if (optionName == "pause")
				{
					GuardNoOptionValue(option);
					project.Configuration.Pause = true;
				}
				else if (optionName == "preenumeratetheories")
				{
					GuardNoOptionValue(option);
					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.PreEnumerateTheories = true;
				}
				else if (optionName == "stoponfail")
				{
					GuardNoOptionValue(option);
					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.StopOnFail = true;
				}
				else if (optionName == "tcp")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -tcp");

					if (!int.TryParse(option.Value, out var port) || port < 1024 || port > 65535)
						throw new ArgumentException($"incorrect argument value for -tcp (must be an integer between 1024 and 65535)");

					TcpPort = port;
				}
				else if (optionName == "trait")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -trait");

					var pieces = option.Value.Split('=');
					if (pieces.Length != 2 || string.IsNullOrEmpty(pieces[0]) || string.IsNullOrEmpty(pieces[1]))
						throw new ArgumentException("incorrect argument format for -trait (should be \"name=value\")");

					var name = pieces[0];
					var value = pieces[1];

					foreach (var projectAssembly in project.Assemblies)
						projectAssembly.Configuration.Filters.IncludedTraits.Add(name, value);
				}
				else if (optionName == "wait")
				{
					GuardNoOptionValue(option);
					project.Configuration.Wait = true;
				}
				else
				{
					// Might be a result output file...
					if (TransformFactory.AvailableTransforms.Any(t => t.ID.Equals(optionName, StringComparison.OrdinalIgnoreCase)))
					{
						if (option.Value == null)
							throw new ArgumentException($"missing filename for {option.Key}");

						EnsurePathExists(option.Value);

						project.Configuration.Output.Add(optionName, option.Value);
					}
					// ...or it might be a reporter (we won't know until later)
					else
					{
						GuardNoOptionValue(option);
						unknownOptions.Add(optionName);
					}
				}
			}

			return project;
		}

		static KeyValuePair<string, string?> PopOption(Stack<string> arguments)
		{
			var option = arguments.Pop();
			string? value = null;

			if (arguments.Count > 0 && !arguments.Peek().StartsWith("-", StringComparison.Ordinal))
				value = arguments.Pop();

			return new KeyValuePair<string, string?>(option, value);
		}

		static void EnsurePathExists(string path)
		{
			var directory = Path.GetDirectoryName(path);

			if (string.IsNullOrEmpty(directory))
				return;

			Directory.CreateDirectory(directory);
		}
	}
}
