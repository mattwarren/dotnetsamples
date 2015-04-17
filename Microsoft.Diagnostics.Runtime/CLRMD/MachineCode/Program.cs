using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Diagnostics.Runtime.Desktop;

namespace MachineCode
{
    class Program
    {
        // Sample based on https://github.com/Microsoft/dotnetsamples/blob/master/Microsoft.Diagnostics.Runtime/CLRMD/docs/MachineCode.md
        // All the common code was copied from the MemStats project "dotnetsamples-master\Microsoft.Diagnostics.Runtime\CLRMD\MemStats\Program.cs"
        static void Main(string[] args)
        {
            string dump, dac;
            if (!TryParseArgs(args, out dump, out dac))
            {
                Usage();
                Environment.Exit(1);
            }

            try
            {
                // Create a ClrRuntime instance from the dump and dac location.  The ClrRuntime
                // object represents a version of CLR loaded in the process.  It contains data
                // such as the managed threads in the process, the AppDomains in the process,
                // the managed heap, and so on.
                //ClrRuntime runtime = CreateRuntime(dump, dac);

                // 1. Get the ClrType object for the type the method is on
                // 2. Get the ClrMethod object for the method
                // 3. Get the offset of the native code
                // 4. Compute the end address by mapping the IL instruction to addresses
                // 5. Disassemble the native code contained in that range (not provided by CLRMD)

                using (DataTarget dt = DataTarget.LoadCrashDump(dump))
                {
                    // Boilerplate.
                    //ClrRuntime runtime = dt.CreateRuntime(dt.ClrVersions.Single().TryDownloadDac());
                    var version = dt.ClrVersions.Single();
                    //{v4.0.30319.18444}
                    //version.Version = new VersionInfo { Major = 4, Minor = 0, Patch = 30319, Revision = 18444 };
                    Console.WriteLine("CLR Version: {0} ({1}), Dac: {2}", version.Version, version.Flavor, version.DacInfo);
                    var dacFileName = version.TryDownloadDac();
                    Console.WriteLine("DacFile: " + Path.GetFileName(dacFileName));
                    Console.WriteLine("DacPath: " + Path.GetDirectoryName(dacFileName));
                    ClrRuntime runtime = dt.CreateRuntime(dacFileName);
                    ClrHeap heap = runtime.GetHeap();

                    PrintDiagnosticInfo(dt, runtime, heap);
                    Console.WriteLine();

                    // Note heap.GetTypeByName doesn't always get you the type, even if it exists, due to
                    // limitations in the dac private apis that ClrMD is written on.  If you have the ClrType
                    // already via other means (heap walking, stack walking, etc), then that's better than
                    // using GetTypeByName:
                    var classNameWithNamespace = "JITterOptimisations.Program";
                    ClrType @class = heap.GetTypeByName(classNameWithNamespace);

                    // Get the method you are looking for.
                    var signature = "JITterOptimisations.Program.Log(System.ConsoleColor, System.String)";
                    ClrMethod @method = @class.Methods.Single(m => m.GetFullSignature() == signature);

                    // This is the first instruction of the JIT'ed (or NGEN'ed) machine code.
                    ulong startAddress = @method.NativeCode;

                    // Unfortunately there's not a great way to get the size of the code, or the end address.
                    // This is partly due to the fact that we don't *have* to put all the JIT'ed code into one
                    // contiguous chunk, though I think an implementation detail is that we actually do.
                    // You are supposed to do code flow analysis like "uf" in windbg to find the size, but
                    // in practice you can use the IL to native mapping:
                    ulong endAddress = @method.ILOffsetMap.Select(entry => entry.EndAddress).Max();

                    var lines =
                        File.ReadAllLines(
                            @"C:\Users\warma11\Documents\Visual Studio 2013\Projects\JITterOptimisations\JITterOptimisations\Program.cs");

                    PrintILToNativeOffsetAlternative(method, lines);

                    // This doesn't seem to work as expected, using alternative method (above)
                    //PrintILToNativeOffsets(method, startAddress, lines);

                    // So the assembly code for the function is is in the range [startAddress, endAddress] inclusive.
                    var count = (int)endAddress + runtime.PointerSize - (int)startAddress;
                    Console.WriteLine("\nCode startAddress 0x{0:X} -> endAddress 0x{1:X} (inclusive), will read {2} bytes", startAddress, endAddress, count);

                    var bytes = new byte[count];
                    int bytesRead;
                    runtime.ReadMemory(startAddress, bytes, count, out bytesRead);
                    if (count != bytesRead)
                        Console.WriteLine("Expected to read {0} bytes, but only read {1}\n", count, bytesRead);
                    else
                        Console.WriteLine("Read read {0} bytes, as expected\n", bytesRead);
                    var fileName = string.Format("result-{0}bit.bin", runtime.PointerSize == 8 ? 64 : 32);
                    if (File.Exists(fileName))
                        File.Delete(fileName);
                    File.WriteAllBytes(fileName, bytes);

                    var filename =
                        @"C:\Users\warma11\Downloads\__GitHub__\dotnetsamples\Microsoft.Diagnostics.Runtime\CLRMD\MachineCode\nasm-2.11.05-win32\ndisasm.exe";
                    var currentFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var arguments = "-b32 " + Path.Combine(currentFolder, fileName); // +" -o " + startAddress;
                    var disassembly = Disassembler.GetDisassembly(filename, arguments, timeoutMsecs: 250);

                    var assemblyData = Disassembler.ProcessDisassembly(disassembly);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unhandled exception:");
                Console.WriteLine(ex);
            }
        }

        private static void PrintILToNativeOffsetAlternative(ClrMethod method, IList<string> lines)
        {
            DesktopModule module = (DesktopModule) @method.Type.Module;
            if (!module.IsPdbLoaded)
            {
                // Have to load the Pdb, if it's not already loaded!!
                string val = module.TryDownloadPdb(null);
                if (val != null)
                    module.LoadPdb(val);
            }

            foreach (var location in module.GetSourceLocationsForMethod(@method.MetadataToken))
            {
                ILOffsetSourceLocation ILLocation = location;
                var ilMaps = @method.ILOffsetMap.Where(il => il.ILOffset == ILLocation.ILOffset);
                Console.WriteLine("{0:X8} -> {1}:{2}",
                                  location.ILOffset,
                                  Path.GetFileName(location.SourceLocation.FilePath),
                                  location.SourceLocation.LineNumber);
                Console.WriteLine("  " + String.Join("\n  ",
                                                     ilMaps.Select(
                                                         ilMap =>
                                                         String.Format("[{0:X8}-{1:X8} ({2:X8}-{3:X8})] ILOffset: {4:X2}",
                                                                       ilMap.StartAddress - @method.NativeCode,
                                                                       ilMap.EndAddress - @method.NativeCode,
                                                                       ilMap.StartAddress, ilMap.EndAddress, ilMap.ILOffset))));
                var indent = 7;
                Console.WriteLine("{0,6}:{1}", location.SourceLocation.LineNumber, lines[location.SourceLocation.LineNumber - 1]);
                Console.WriteLine(new string(' ', location.SourceLocation.ColStart - 1 + indent) +
                                  new string('*', location.SourceLocation.ColEnd - location.SourceLocation.ColStart));
            }
            Console.WriteLine();
        }

        private static void PrintILToNativeOffsets(ClrMethod method, ulong startAddress, IList<string> lines)
        {
            Console.WriteLine("IL -> Native Offsets:");
            Console.WriteLine("\t" + String.Join("\n\t", @method.ILOffsetMap));
            foreach (ILToNativeMap il in @method.ILOffsetMap)
            {
                SourceLocation sourceLocation = null;
                try
                {
                    Console.WriteLine(
                        "\nPrintILToNativeOffsets, il.Offset: {0,10} ({1,3:N0}), il.StartAddress: {2} ({2,8:X8})",
                        "0x" + il.ILOffset.ToString("X2"), il.ILOffset, il.StartAddress);
                    if (il.ILOffset < 0) // FFFF FFFX is 4,294,967,280
                        continue;
                    sourceLocation = @method.GetSourceLocationForOffset(il.StartAddress);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    continue;
                }

                var sourceInfo = "<UNKNOWN>";
                if (sourceLocation != null)
                {
                    if (sourceLocation.LineNumber == sourceLocation.LineNumberEnd)
                    {
                        sourceInfo = String.Format("{0}:{1} (Columns {2}->{3})",
                                                   Path.GetFileName(sourceLocation.FilePath),
                                                   sourceLocation.LineNumber, sourceLocation.ColStart,
                                                   sourceLocation.ColEnd);
                    }
                    else
                    {
                        sourceInfo = String.Format("{0}: {1}->{2} ({3}->{4})",
                                                   Path.GetFileName(sourceLocation.FilePath),
                                                   sourceLocation.LineNumber, sourceLocation.LineNumberEnd,
                                                   sourceLocation.ColStart, sourceLocation.ColEnd);
                    }
                }

                Console.WriteLine("{0,10} ({1,3:N0}) - [{2:X8}-{3:X8} ({4:X8}-{5:X8})] {6}",
                                  "0x" + il.ILOffset.ToString("X2"), il.ILOffset,
                                  il.StartAddress - startAddress, il.EndAddress - startAddress,
                                  il.StartAddress, il.EndAddress, sourceInfo);
                if (sourceLocation != null && il.ILOffset >= 0)
                {
                    var indent = 7;
                    Console.WriteLine("{0,6}:{1}", sourceLocation.LineNumber, lines[sourceLocation.LineNumber - 1]);
                    Console.WriteLine(new string(' ', sourceLocation.ColStart - 1 + indent) +
                                      new string('*', sourceLocation.ColEnd - sourceLocation.ColStart));
                }
            }
        }

        private static void PrintDiagnosticInfo(DataTarget dt, ClrRuntime runtime, ClrHeap heap)
        {
            Console.WriteLine("DataTarget Info:");
            Console.WriteLine("  ClrVersions: " + String.Join(", ", dt.ClrVersions));
            Console.WriteLine("  IsMinidump: " + dt.IsMinidump);
            Console.WriteLine("  Architecture: " + dt.Architecture);
            Console.WriteLine("  PointerSize: " + dt.PointerSize);
            Console.WriteLine("  SymbolPath: " + dt.GetSymbolPath());

            Console.WriteLine("ClrRuntime Info:");
            Console.WriteLine("  ServerGC: " + runtime.ServerGC);
            Console.WriteLine("  HeapCount: " + runtime.HeapCount);
            Console.WriteLine("  Thread Count: " + runtime.Threads.Count);

            Console.WriteLine("ClrRuntime Modules:");
            foreach (var module in runtime.EnumerateModules())
            {
                Console.WriteLine("  {0,26} Id:{1}, {2,10:N0} bytes @ 0x{3:X8}",
                                  Path.GetFileName(module.FileName), module.AssemblyId, module.Size, module.ImageBase);
            }

            Console.WriteLine("ClrHeap Info:");
            Console.WriteLine("  TotalHeapSize: " + heap.TotalHeapSize);
            Console.WriteLine("  Segments: " + heap.Segments.Count);
            Console.WriteLine("  Gen0 Size: " + heap.GetSizeByGen(0));
            Console.WriteLine("  Gen1 Size: " + heap.GetSizeByGen(1));
            Console.WriteLine("  Gen2 Size: " + heap.GetSizeByGen(2));
            Console.WriteLine("  Gen3 Size: " + heap.GetSizeByGen(3));
        }

        public static bool TryParseArgs(string[] args, out string dump, out string dac)
        {
            dump = null;
            dac = null;

            foreach (string arg in args)
            {
                if (dump == null)
                {
                    dump = arg;
                }
                else if (dac == null)
                {
                    dac = arg;
                }
                else
                {
                    Console.WriteLine("Too many arguments.");
                    return false;
                }
            }

            return dump != null;
        }

        public static void Usage()
        {
            string fn = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
            Console.WriteLine("Usage: {0} crash.dmp [dac_file_name]", fn);
        }
    }
}
