using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using SharpPdb.Managed;
namespace BaGetter.Core;

// Based off: https://github.com/NuGet/NuGetGallery/blob/master/src/NuGetGallery/Services/SymbolPackageUploadService.cs
// Based off: https://github.com/NuGet/NuGet.Jobs/blob/master/src/Validation.Symbols/SymbolsValidatorService.cs#L44
public class SymbolIndexingService : ISymbolIndexingService
{
    private static readonly HashSet<string> ValidSymbolPackageContentExtensions = new HashSet<string>
    {
        ".pdb",
        ".nuspec",
        ".xml",
        ".psmdcp",
        ".rels",
        ".p7s"
    };

    private readonly IPackageDatabase _packages;
    private readonly ISymbolStorageService _storage;
    private readonly ILogger<SymbolIndexingService> _logger;

    public SymbolIndexingService(
        IPackageDatabase packages,
        ISymbolStorageService storage,
        ILogger<SymbolIndexingService> logger)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    // could be replaced with try catch portable pdb... 
    private static bool IsPortablePdb(Stream pdbStream)
    {
        // Check for 'BSJB' signature at the start of the file
        byte[] bsjbSignature = { 0x42, 0x53, 0x4A, 0x42 };
        byte[] fileHeader = new byte[bsjbSignature.Length];
        pdbStream.Read(fileHeader, 0, fileHeader.Length);
        pdbStream.Seek(0, SeekOrigin.Begin); // Reset stream position

        return bsjbSignature.SequenceEqual(fileHeader);
    }


    public async Task<SymbolIndexingResult> IndexAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var symbolPackage = new PackageArchiveReader(stream, leaveStreamOpen: true);
            var pdbPaths = await GetSymbolPackagePdbPathsOrNullAsync(symbolPackage, cancellationToken);
            if (pdbPaths == null)
            {
                return SymbolIndexingResult.InvalidSymbolPackage;
            }

            // Ensure a corresponding NuGet package exists.
            var packageId = symbolPackage.NuspecReader.GetId();
            var packageVersion = symbolPackage.NuspecReader.GetVersion();

            var package = await _packages.FindOrNullAsync(packageId, packageVersion, includeUnlisted: true, cancellationToken);
            if (package == null)
            {
                return SymbolIndexingResult.PackageNotFound;
            }

            using var pdbs = new PdbList();
            // Extract the portable PDBs from the snupkg. Nothing is persisted until after all
            // PDBs have been extracted and validated sucessfully.
            foreach (var pdbPath in pdbPaths)
            {
                var portablePdb = await ExtractPortablePdbAsync(symbolPackage, pdbPath, cancellationToken);
                if (portablePdb == null)
                {
                    return SymbolIndexingResult.InvalidSymbolPackage;
                }

                pdbs.Add(portablePdb);
            }

            // Persist the portable PDBs to storage.
            foreach (var pdb in pdbs)
            {
                await _storage.SavePortablePdbContentAsync(pdb.Filename, pdb.Key, pdb.Content, cancellationToken);
            }

            return SymbolIndexingResult.Success;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to index symbol package due to exception");
            return SymbolIndexingResult.InvalidSymbolPackage;
        }
    }

    private async Task<IReadOnlyList<string>> GetSymbolPackagePdbPathsOrNullAsync(
        PackageArchiveReader symbolPackage,
        CancellationToken cancellationToken)
    {
        try
        {
            await symbolPackage.ValidatePackageEntriesAsync(cancellationToken);

            var files = (await symbolPackage.GetFilesAsync(cancellationToken)).ToList();

            // Ensure there are no unexpected file extensions within the symbol package.
            if (!AreSymbolFilesValid(files))
            {
                return null;
            }

            return files.Where(p => Path.GetExtension(p) == ".pdb").ToList();
        }
        catch (Exception)
        {
            // TODO: ValidatePackageEntries throws PackagingException.
            // Add better logging.
            return null;
        }
    }

    private bool AreSymbolFilesValid(IReadOnlyList<string> entries)
    {
        // TODO: Validate that all PDBs are portable. See: https://github.com/NuGet/NuGetGallery/blob/master/src/NuGetGallery/Services/SymbolPackageService.cs#L174
        bool IsValidSymbolFileInfo(FileInfo file)
        {
            if (string.IsNullOrEmpty(file.Name)) return false;
            if (string.IsNullOrEmpty(file.Extension)) return false;
            if (!ValidSymbolPackageContentExtensions.Contains(file.Extension)) return false;

            return true;
        }

        return entries.Select(e => new FileInfo(e)).All(IsValidSymbolFileInfo);
    }

    private static async Task<PortablePdb> ExtractPortablePdbAsync(
        PackageArchiveReader symbolPackage,
        string pdbPath,
        CancellationToken cancellationToken)
    {
        // TODO: Validate that the PDB has a corresponding DLL
        // See: https://github.com/NuGet/NuGet.Jobs/blob/master/src/Validation.Symbols/SymbolsValidatorService.cs#L170
        Stream pdbStream = null;
        PortablePdb result = null;

        try
        {
            using var rawPdbStream = await symbolPackage.GetStreamAsync(pdbPath, cancellationToken);
            using var rawPdbStream2 = await symbolPackage.GetStreamAsync(pdbPath, cancellationToken);
            //TODO solution from file or any other not that xD
            // make "temp file you need to find the guuid"
            pdbStream = await rawPdbStream.AsTemporaryFileStreamAsync(cancellationToken);

            var tmpStream = rawPdbStream;
            string signature = "";
            string fileName = "";
            string key = "";
            var isPortablePdb = IsPortablePdb(pdbStream);


            //todo fix these
            if (isPortablePdb)
            {
                using (var pdbReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen))
                {

                    var reader = pdbReaderProvider.GetMetadataReader();
                    var id = new BlobContentId(reader.DebugMetadataHeader.Id);

                    signature = id.Guid.ToString("N").ToUpperInvariant();
                }
            }
            else
            {

                string fullPath = Path.GetFullPath(pdbPath);
                Console.WriteLine($"Full path: {fullPath}");
                string outputPath = Path.Combine(Path.GetTempPath(), "pdb", Path.GetFileName(pdbPath));
                using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    await rawPdbStream2.CopyToAsync(fileStream);
                }

                using (var pdbReader = PdbFileReader.OpenPdb(outputPath))
                {
                    signature = pdbReader.Guid.ToString("N").ToUpperInvariant();

                };
                Console.WriteLine("here");
            }


            fileName = Path.GetFileName(pdbPath).ToLowerInvariant();
            key = $"{signature}ffffffff";

            pdbStream.Position = 0;
            result = new PortablePdb(fileName, key, pdbStream);
        }
        catch (Exception ex)
        {
            var test = ex.Message;
            Console.WriteLine("exception new: " + test);
        }
        finally
        {
            if (result == null)
            {
                pdbStream?.Dispose();
            }
        }

        return result;
    }

    private class PortablePdb : IDisposable
    {
        public PortablePdb(string filename, string key, Stream content)
        {
            Filename = filename;
            Key = key;
            Content = content;
        }

        public string Filename { get; }
        public string Key { get; }
        public Stream Content { get; }

        public void Dispose() => Content.Dispose();
    }

    private class PdbList : List<PortablePdb>, IDisposable
    {
        public void Dispose()
        {
            foreach (var pdb in this)
            {
                pdb.Dispose();
            }
        }
    }
}
