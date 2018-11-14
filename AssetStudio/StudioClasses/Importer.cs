using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static AssetStudio.Studio;

namespace AssetStudio
{
    internal static class Importer
    {
        public static List<string> importFiles = new List<string>(); //files to load
        public static HashSet<string> importFilesHash = new HashSet<string>(); //to improve the loading speed
        public static HashSet<string> assetsfileListHash = new HashSet<string>(); //to improve the loading speed

        public static void LoadFile(string fullName)
        {
            switch (CheckFileType(fullName, out EndianBinaryReader reader))
            {
                case FileType.AssetsFile:
                    LoadAssetsFile(fullName, reader);
                    break;
                case FileType.BundleFile:
                    LoadBundleFile(fullName, reader);
                    break;
                case FileType.WebFile:
                    LoadWebFile(fullName, reader);
                    break;
            }
        }

        private static void LoadAssetsFile(string fullName, EndianBinaryReader reader, string parentPath = null)
        {
            string fileName = Path.GetFileName(fullName);

            StatusStripUpdate("Loading " + fileName);

            Debug.Assert(fileName != null, nameof(fileName) + " != null");

            if (assetsfileListHash.Contains(fileName.ToUpper()))
            {
                return;
            }

            var assetsFile = new AssetsFile(fullName, reader);

            if (assetsFile.valid)
            {
                assetsFile.parentPath = parentPath;
                assetsFileList.Add(assetsFile);
                assetsfileListHash.Add(assetsFile.upperFileName);

                #region for 2.6.x find mainData and get string version

                if (assetsFile.header.m_Version == 6 && fileName != "mainData")
                {
                    AssetsFile mainDataFile = assetsFileList.Find(aFile => aFile.fileName == "mainData");

                    if (mainDataFile != null)
                    {
                        assetsFile.unityVersion = mainDataFile.unityVersion;
                        assetsFile.version = mainDataFile.version;
                        assetsFile.buildType = mainDataFile.buildType;
                    }
                    else if (File.Exists(Path.GetDirectoryName(fullName) + "\\mainData"))
                    {
                        mainDataFile = new AssetsFile(Path.GetDirectoryName(fullName) + "\\mainData", new EndianBinaryReader(File.OpenRead(Path.GetDirectoryName(fullName) + "\\mainData")));
                        assetsFile.unityVersion = mainDataFile.unityVersion;
                        assetsFile.version = mainDataFile.version;
                        assetsFile.buildType = mainDataFile.buildType;
                    }
                }

                #endregion

                var value = 0;

                foreach (FileIdentifier sharedFile in assetsFile.m_Externals)
                {
                    string sharedFilePath = Path.GetDirectoryName(fullName) + "\\" + sharedFile.fileName;
                    string sharedFileName = sharedFile.fileName;

                    if (importFilesHash.Contains(sharedFileName.ToUpper()))
                    {
                        continue;
                    }

                    if (!File.Exists(sharedFilePath))
                    {
                        string[] findFiles = Directory.GetFiles(Path.GetDirectoryName(fullName), sharedFileName, SearchOption.AllDirectories);
                        if (findFiles.Length > 0)
                        {
                            sharedFilePath = findFiles[0];
                        }
                    }

                    if (!File.Exists(sharedFilePath))
                    {
                        continue;
                    }

                    importFiles.Add(sharedFilePath);
                    importFilesHash.Add(sharedFileName.ToUpper());

                    value++;
                }

                if (value > 0)
                {
                    ProgressBarIncrementMaximum(value);
                }
            }
            else
            {
                reader.Dispose();
            }
        }

        private static void LoadBundleFile(string fullName, EndianBinaryReader reader, string parentPath = null)
        {
            string fileName = Path.GetFileName(fullName);

            StatusStripUpdate("Decompressing " + fileName);

            var bundleFile = new BundleFile(reader, fullName);
            reader.Dispose();

            foreach (StreamFile file in bundleFile.fileList)
            {
                if (assetsfileListHash.Contains(file.fileName.ToUpper()))
                {
                    continue;
                }

                StatusStripUpdate("Loading " + file.fileName);

                var assetsFile = new AssetsFile(Path.GetDirectoryName(fullName) + "\\" + file.fileName, new EndianBinaryReader(file.stream));

                if (assetsFile.valid)
                {
                    assetsFile.parentPath = parentPath ?? fullName;

                    if (assetsFile.header.m_Version == 6) //2.6.x and earlier don't have a string version before the preload table
                    {
                        //make use of the bundle file version
                        assetsFile.unityVersion = bundleFile.versionEngine;
                        assetsFile.version = Regex.Matches(bundleFile.versionEngine, @"\d").Cast<Match>().Select(m => int.Parse(m.Value)).ToArray();
                        assetsFile.buildType = Regex.Replace(bundleFile.versionEngine, @"\d", "").
                            Split(new[]
                            {
                                "."
                            }, StringSplitOptions.RemoveEmptyEntries);
                    }

                    assetsFileList.Add(assetsFile);
                    assetsfileListHash.Add(assetsFile.upperFileName);
                }
                else
                {
                    resourceFileReaders.Add(assetsFile.upperFileName, assetsFile.reader);
                }
            }
        }

        private static void LoadWebFile(string fullName, EndianBinaryReader reader)
        {
            string fileName = Path.GetFileName(fullName);

            StatusStripUpdate("Loading " + fileName);

            var webFile = new WebFile(reader);
            reader.Dispose();

            foreach (StreamFile file in webFile.fileList)
            {
                string dummyName = Path.GetDirectoryName(fullName) + "\\" + file.fileName;

                switch (CheckFileType(file.stream, out reader))
                {
                    case FileType.AssetsFile:
                        LoadAssetsFile(dummyName, reader, fullName);
                        break;
                    case FileType.BundleFile:
                        LoadBundleFile(dummyName, reader, fullName);
                        break;
                    case FileType.WebFile:
                        LoadWebFile(dummyName, reader);
                        break;
                }

                resourceFileReaders.Add(file.fileName.ToUpper(), reader);
            }
        }

        public static void MergeSplitAssets(string dirPath)
        {
            string[] splitFiles = Directory.GetFiles(dirPath, "*.split0");

            foreach (string splitFile in splitFiles)
            {
                string destFile = Path.GetFileNameWithoutExtension(splitFile);
                string destPath = Path.GetDirectoryName(splitFile) + "\\";
                string destFull = destPath + destFile;

                if (File.Exists(destFull))
                {
                    continue;
                }

                string[] splitParts = Directory.GetFiles(destPath, destFile + ".split*");

                using (FileStream destStream = File.Create(destFull))
                {
                    for (var i = 0; i < splitParts.Length; i++)
                    {
                        string splitPart = destFull + ".split" + i;
                        using (FileStream sourceStream = File.OpenRead(splitPart))
                            sourceStream.CopyTo(destStream);
                    }
                }
            }
        }
    }
}