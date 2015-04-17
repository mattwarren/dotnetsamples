using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Diagnostics;
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
                    Console.WriteLine("DacFile: " + Path.GetDirectoryName(dacFileName));
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

                    // Find out whether the method was JIT'ed or NGEN'ed (if you care):
                    MethodCompilationType compileType = @method.CompilationType;
                    Console.WriteLine("{0} was JIT'ed by {1}", @method.ToString(), compileType);

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

                    PrintILToNativeOffsets(method, startAddress, lines);

                    // This is what we expect it to be
                    //--- c:\Users\warma11\Documents\Visual Studio 2013\Projects\JITterOptimisations\JITterOptimisations\Program.cs 
                    //    45:             return Math.Sqrt(value);
                    //00000000 55                   push        ebp 
                    //00000001 8B EC                mov         ebp,esp 
                    //00000003 DD 45 08             fld         qword ptr [ebp+8] 
                    //00000006 D9 FA                fsqrt 
                    //00000008 5D                   pop         ebp 
                    //00000009 C2 08 00             ret         8 

                    // So the assembly code for the function is is in the range [startAddress, endAddress] inclusive.
                    var count = (int)endAddress + runtime.PointerSize - (int)startAddress;
                    Console.WriteLine("\nCode startAddress 0x{0:X} -> endAddress 0x{1:X} (inclusive), will read {2} bytes", startAddress, endAddress, count);

                    byte[] bytes = new byte[count];
                    int bytesRead;
                    runtime.ReadMemory(startAddress, bytes, count, out bytesRead);
                    if (count != bytesRead)
                        Console.WriteLine("Expected to read {0} bytes, but only read {1}", count, bytesRead);
                    else
                        Console.WriteLine("Read read {0} bytes, as expected", bytesRead);
                    var fileName = string.Format("result-{0}bit.bin", runtime.PointerSize == 8 ? 64 : 32);
                    if (File.Exists(fileName))
                        File.Delete(fileName);
                    File.WriteAllBytes(fileName, bytes);

                    Console.WriteLine();

                    var filename =
                        @"C:\Users\warma11\Downloads\__GitHub__\dotnetsamples\Microsoft.Diagnostics.Runtime\CLRMD\MachineCode\nasm-2.11.05-win32\ndisasm.exe";
                    var currentFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var arguments = "-b32 " + Path.Combine(currentFolder, fileName); // +" -o " + startAddress;
                    var disassembly = GetDisassembly(filename, arguments, timeoutMsecs: 250);
                    //Console.WriteLine(disassembly);

                    var assemblyData = ProcessDisassembly(disassembly);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unhandled exception:");
                Console.WriteLine(ex);
            }
        }

        private static void PrintILToNativeOffsets(ClrMethod method, ulong startAddress, string[] lines)
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
                if (sourceLocation != null && il.ILOffset >= 0) // FFFF FFFX (-ve) appear to be special cases
                {
                    var indent = 7;
                    Console.WriteLine("{0,6}:{1}", sourceLocation.LineNumber, lines[sourceLocation.LineNumber - 1]);
                    Console.WriteLine(new string(' ', sourceLocation.ColStart - 1 + indent) +
                                      new string('*', sourceLocation.ColEnd - sourceLocation.ColStart));
                }
                //Console.WriteLine();
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

        // From http://stackoverflow.com/questions/139593/processstartinfo-hanging-on-waitforexit-why/7608823#7608823
        private static string GetDisassembly(string filename, string arguments, int timeoutMsecs)
        {
            using (Process process = new Process())
            {
                process.StartInfo.FileName = filename;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                StringBuilder output = new StringBuilder();
                StringBuilder error = new StringBuilder();

                using (AutoResetEvent outputWaitHandle = new AutoResetEvent(false))
                using (AutoResetEvent errorWaitHandle = new AutoResetEvent(false))
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data == null)
                        {
                            if (outputWaitHandle != null) outputWaitHandle.Set();
                        }
                        else
                        {
                            output.AppendLine(e.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data == null)
                        {
                            if (errorWaitHandle != null) errorWaitHandle.Set();
                        }
                        else
                        {
                            error.AppendLine(e.Data);
                        }
                    };

                    process.Start();

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (process.WaitForExit(timeoutMsecs) &&
                        outputWaitHandle.WaitOne(timeoutMsecs) &&
                        errorWaitHandle.WaitOne(timeoutMsecs))
                    {
                        // Process completed. Check process.ExitCode here.
                    }
                    else
                    {
                        // Timed out.
                        Console.WriteLine("TIMED OUT!!");
                    }
                }
                if (error.Length > 0)
                    Console.WriteLine(error.ToString());

                return output.ToString();
            }
        }

        private static List<AssemblyLine> ProcessDisassembly(string disassembly)
        {
            var assemblyData = new List<AssemblyLine>();
            var disassemblyLines = disassembly.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in disassemblyLines)
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    Console.WriteLine("Unexpected line: " + line);
                    continue;
                }

                var address = Convert.ToUInt32(parts[0], 16);
                var rawData = parts[1];
                var textStart = line.IndexOf(parts[2]);
                var text = line.Substring(textStart);

                // Test our error checking!!!
                //if (rawData == "53")
                //    text = "push blah";

                var newData = new AssemblyLine { Address = address, RawData = rawData, Text = text };
                Console.WriteLine(newData);
                assemblyData.Add(newData);
            }

            int lineNum = 0;
            foreach (var line in disassemblyLines)
            {
                var other = assemblyData[lineNum];
                if (other.ToString() != line)
                    Console.WriteLine("Error on line {0}\nExpected:{1}\n     Got:{2}", lineNum, line, other.ToString());
                lineNum++;
            }
            return assemblyData;
        }

        private class AssemblyLine
        {
            public uint Address { get; set; }
            public string RawData { get; set; }
            public string Text { get; set; }

            public override string ToString()
            {
                //00000000  55                push ebp
                //00000001  8BEC              mov ebp,esp
                //0000000A  E879BBFA70        call dword 0x70fabb88
                return String.Format("{0:X8}  {1}{2}", Address, RawData.PadRight(18), Text);
            }
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
