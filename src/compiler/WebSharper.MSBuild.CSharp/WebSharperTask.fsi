// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2016 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

namespace WebSharper.MSBuild.CSharp

open Microsoft.Build.Framework
open Microsoft.Build.Utilities

/// Implements MSBuild logic used in `WebSharper.targets`.
[<Sealed>]
type WebSharperTask =
    inherit ToolTask

    new : unit -> WebSharperTask
//
//    /// Used to specify which "method" to call.
//    member Command : string with get, set

    /// MSBuild $(Configuration) property.
    member Configuration : string with get, set

    /// Path to the XML documentation file that the Interface Generator should generate.
    member DocumentationFile : string with get, set

    /// Propagating EmbeddedResource group from MSBuild.
    member EmbeddedResources : ITaskItem [] with get, set

    member OutputAssembly : ITaskItem with get, set
    member DefineConstants : string with get, set
    member NoStandardLib : string with get, set
    member Sources : ITaskItem [] with get, set
    
    /// Item input for item commands.
    member References : ITaskItem [] with get, set

//    /// Item output for item commands.
//    member ItemOutput : ITaskItem [] with get, set

    /// Path to an `.snk` strong name key file, if any.
    member KeyOriginatorFile : string with get, set

    /// MSBuild $(MSBuildProjectFullPath) property.
    member MSBuildProjectFullPath : string with get, set

    /// MSBuild $(MSBuildProjectDirectory) property.
    member MSBuildProjectDirectory : string with get, set

    /// The project $(Name) property, typically set in F# projects.
    member Name : string with get, set

    /// The output path of MSBuild compilation.
    member OutputPath : string with get, set

//    /// Copy-local file list.
//    member ReferenceCopyLocalPaths : ITaskItem [] with get, set

    /// Path to the directory containing Web.config, used by the "Unpack" command,
    /// and also to detect Web projects.
    member WebProjectOutputDir : string with get, set

    /// Output directory for the Bundle task.
    member WebSharperBundleOutputDir : string with get, set

    /// Used as root for HTML project output.
    member WebSharperHtmlDirectory : string with get, set

    /// Specifies which project type is being built.
    member WebSharperProject : string with get, set

    /// Outputs all JS compilation errors as warnings if set to True.
    member WebSharperErrorsAsWarnings : string with get, set

    /// Outputs only reachable code by compile-time analysis for bundles.
    member WebSharperDeadCodeElimination : string with get, set
    
    /// Specifies if source map is generated and source files are included
    /// in the assembly as resources.
    member WebSharperSourceMap : string with get, set

    /// Specifies if .d.ts files are unpacked along with the .js output.
    member WebSharperTypeScriptDeclaration : string with get, set

    /// Specifies if online resource links are downloaded for local use.
    member WebSharperDownloadResources : string with get, set

    /// Specifies if closure analysis for avoiding memory leaks is done.
    member WebSharperAnalyzeClosures : string with get, set

    member ZafirToolPath : string with get, set

    member TargetType : string with get, set

    member NoConfig : string with get, set

    member DebugType : string with get, set

    member SubsystemVersion : string with get, set

    member LangVersion : string with get, set