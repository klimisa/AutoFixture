#r @"packages/FAKE.Core/tools/FakeLib.dll"

open Fake
open Fake.AssemblyInfoFile
open Fake.Testing
open System
open System.Diagnostics;
open System.Text.RegularExpressions

let releaseFolder = "ReleaseArtifacts"
let nunitToolsFolder = "Packages/NUnit.Runners.2.6.2/tools"
let nuGetOutputFolder = "NuGetPackages"
let solutionsToBuild = !! "Src/AutoFixture.AllProjects.sln"

type GitVersion = { apiVersion:string; nugetVersion:string }
let getGitVersion = 
    let desc = Git.CommandHelper.runSimpleGitCommand "" "describe --tags --long --match=v*"
    // Example for regular: v3.50.2-288-g64fd5c5b, for prerelease: v3.50.2-alpha1-288-g64fd5c5b
    let result = Regex.Match(desc, @"^v(?<maj>\d+)\.(?<min>\d+)\.(?<rev>\d+)(?<pre>-\w+\d*)?-(?<num>\d+)-g(?<sha>[a-z0-9]+)$", RegexOptions.IgnoreCase).Groups
    let getMatch (name:string) = result.[name].Value
    
    let assemblyVer = sprintf "%s.%s.%s" (getMatch "maj") (getMatch "min") (getMatch "rev")    
    let apiVer = sprintf "%s.0" assemblyVer
    let nugetVer = sprintf "%s%s%s" assemblyVer (getMatch "pre") (match getMatch "num" with "0" -> "" | commitsSinceTag -> "." + commitsSinceTag)
    
    { apiVersion = apiVer ; nugetVersion = nugetVer }

let build target configuration version infoVersion =
    solutionsToBuild
    |> Seq.iter (fun s -> build (fun p -> { p with Verbosity = Some(Minimal)
                                                   Targets = [target]
                                                   Properties = 
                                                      [
                                                          "Configuration", configuration
                                                          "AssemblyVersion", version
                                                          "InformationalVersion", infoVersion
                                                      ] }) s)
    |> ignore

let rebuild configuration =
    let version = 
        match getBuildParamOrDefault "Version" "git" with
        | "git"    -> getGitVersion
        | custom -> { apiVersion = custom; nugetVersion = match getBuildParam "NugetVersion" with "" -> custom | v -> v }

    build "Rebuild" configuration version.apiVersion version.nugetVersion


Target "CleanAll"           (fun _ -> ())
Target "CleanVerify"        (fun _ -> build "Clean" "Verify" "" "")
Target "CleanReleaseFolder" (fun _ -> CleanDir releaseFolder)

Target "Verify" (fun _ -> rebuild "Verify")

Target "BuildOnly" (fun _ -> rebuild "Release")
Target "TestOnly" (fun _ ->
    let configuration = getBuildParamOrDefault "Configuration" "Release"
    let parallelizeTests = getBuildParamOrDefault "ParallelizeTests" "False" |> Convert.ToBoolean
    let maxParallelThreads = getBuildParamOrDefault "MaxParallelThreads" "0" |> Convert.ToInt32
    let parallelMode = if parallelizeTests then ParallelMode.All else ParallelMode.NoParallelization
    let maxThreads = if maxParallelThreads = 0 then CollectionConcurrencyMode.Default else CollectionConcurrencyMode.MaxThreads(maxParallelThreads)

    let testAssemblies = !! (sprintf "Src/*Test/bin/%s/*Test.dll" configuration)
                         -- (sprintf "Src/AutoFixture.NUnit*.*Test/bin/%s/*Test.dll" configuration)

    testAssemblies
    |> xUnit2 (fun p -> { p with Parallel = parallelMode
                                 MaxThreads = maxThreads })

    let nunit2TestAssemblies = !! (sprintf "Src/AutoFixture.NUnit2.*Test/bin/%s/*Test.dll" configuration)

    nunit2TestAssemblies
    |> NUnit (fun p -> { p with StopOnError = false
                                OutputFile = "NUnit2TestResult.xml" })

    let nunit3TestAssemblies = !! (sprintf "Src/AutoFixture.NUnit3.UnitTest/bin/%s/Ploeh.AutoFixture.NUnit3.UnitTest.dll" configuration)

    nunit3TestAssemblies
    |> NUnit3 (fun p -> { p with StopOnError = false
                                 ResultSpecs = ["NUnit3TestResult.xml;format=nunit2"] })
)

Target "BuildAndTestOnly" (fun _ -> ())
Target "Build" (fun _ -> ())
Target "Test"  (fun _ -> ())

Target "CopyToReleaseFolder" (fun _ ->
    let nuGetPackageScripts = !! "NuGet/*.ps1" ++ "NuGet/*.txt" ++ "NuGet/*.pp" |> List.ofSeq
    let filesToCopy = [ nunitToolsFolder @@ "lib/nunit.core.interfaces.dll" ] @ nuGetPackageScripts

    filesToCopy
    |> CopyFiles releaseFolder
)

Target "CleanNuGetPackages" (fun _ ->
    CleanDir nuGetOutputFolder
)

Target "NuGetPack" (fun _ ->
    let version = FileVersionInfo.GetVersionInfo(releaseFolder @@ "AutoFixture\\net45\\Ploeh.AutoFixture.dll").ProductVersion

    let nuSpecFiles = !! "NuGet/*.nuspec"

    nuSpecFiles
    |> Seq.iter (fun f -> NuGet (fun p -> { p with Version = version
                                                   WorkingDir = releaseFolder
                                                   OutputPath = nuGetOutputFolder
                                                   SymbolPackage = NugetSymbolPackage.Nuspec }) f)
)

let publishPackagesToNuGet apiFeed symbolFeed nugetKey =
    let packages = !! (sprintf "%s/*.nupkg" nuGetOutputFolder)     

    packages
    |> Seq.map (fun p ->
        let isSymbolPackage = p.EndsWith "symbols.nupkg"
        let feed =
            match isSymbolPackage with
            | true -> symbolFeed
            | false -> apiFeed

        let meta = GetMetaDataFromPackageFile p
        let version = 
            match isSymbolPackage with
            | true -> sprintf "%s.symbols" meta.Version
            | false -> meta.Version

        (meta.Id, version, feed))
    |> Seq.iter (fun (id, version, feed) -> NuGetPublish (fun p -> { p with PublishUrl = feed
                                                                            AccessKey = nugetKey
                                                                            OutputPath = nuGetOutputFolder
                                                                            Project = id
                                                                            Version = version }))    

Target "PublishNuGetPreRelease" (fun _ -> publishPackagesToNuGet 
                                            "https://www.myget.org/F/autofixture/api/v2/package" 
                                            "https://www.myget.org/F/autofixture/symbols/api/v2/package"
                                            (getBuildParam "NuGetPreReleaseKey"))

Target "PublishNuGetRelease" (fun _ -> publishPackagesToNuGet
                                            "https://www.nuget.org/api/v2/package"
                                            "https://nuget.smbsrc.net/"
                                            (getBuildParam "NuGetReleaseKey"))

Target "CompleteBuild" (fun _ -> ())
Target "PublishNuGetAll" (fun _ -> ()) 

"CleanVerify"        ==> "CleanAll"
"CleanReleaseFolder" ==> "CleanAll"

"CleanAll"  ==> "Verify"

"Verify"                ==> "Build"
"BuildOnly"             ==> "Build"

"Build"    ==> "Test"
"TestOnly" ==> "Test"

"BuildOnly" 
    ==> "TestOnly"
    ==> "BuildAndTestOnly"

"Test" ==> "CopyToReleaseFolder"

"CleanNuGetPackages"  ==> "NuGetPack"
"CopyToReleaseFolder" ==> "NuGetPack"

"NuGetPack" ==> "CompleteBuild"

"NuGetPack" ==> "PublishNuGetRelease"
"NuGetPack" ==> "PublishNuGetPreRelease"

"PublishNuGetRelease"    ==> "PublishNuGetAll"
"PublishNuGetPreRelease" ==> "PublishNuGetAll"



RunTargetOrDefault "CompleteBuild"
