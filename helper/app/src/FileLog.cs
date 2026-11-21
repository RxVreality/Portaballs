using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Portaballs.Helper
{
    /// <summary>
    /// A plain text log beside the program (helper.log). One line per event, newest at the bottom.
    /// When it passes 2 MB it is renamed helper.log.1 and a fresh one is started.
    /// </summary>
    internal sealed class FileLog : IDisposable
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        private readonly object _gate = new object();
        private readonly string _path;
        private readonly bool _echo;
        private StreamWriter _writer;

        /// <param name="path">File to write, or null for no file (tests).</param>
        /// <param name="echoToConsole">Also print each line (the --console switch).</param>
        public FileLog(string path, bool echoToConsole)
        {
            _path = path;
            _echo = echoToConsole;
            if (_path != null) Open();
        }

        public string Path { get { return _path; } }

        public void Info(string message)
        {
            Write("     ", message);
        }

        public void Warn(string message)
        {
            Write("WARN ", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        private void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " " + message;
            lock (_gate)
            {
                if (_echo)
                {
                    try { Console.WriteLine(line); } catch (Exception) { }
                }
                if (_writer == null) return;
                try
                {
                    _writer.WriteLine(line);
                    if (_writer.BaseStream.Length > MaxBytes) Rotate();
                }
                catch (Exception)
                {
                    // A log that cannot be written must never stop the helper.
                }
            }
        }

        private void Open()
        {
            try
            {
                FileStream stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(false));
                _writer.AutoFlush = true;
            }
            catch (Exception)
            {
                _writer = null;
            }
        }

        private void Rotate()
        {
            _writer.Dispose();
            _writer = null;
            try
            {
                string old = _path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_path, old);
            }
            catch (Exception)
            {
            }
            Open();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_writer != null)
                {
                    try { _writer.Dispose(); } catch (Exception) { }
                    _writer = null;
                }
            }
        }
    }
}
