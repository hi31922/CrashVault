using System;
using System.IO;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace CrashVault
{
    internal sealed class SqliteDatabase : IDisposable
    {
        private IntPtr handle;

        public SqliteDatabase(string path)
        {
            int result = SqliteNative.sqlite3_open16(path, out handle);
            try
            {
                Check(result);
                Check(SqliteNative.sqlite3_busy_timeout(handle, 3000));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public SqliteStatement Prepare(string sql)
        {
            IntPtr statement;
            Check(SqliteNative.sqlite3_prepare16_v2(handle, sql, -1, out statement, IntPtr.Zero));
            return new SqliteStatement(this, statement);
        }

        public void Execute(string sql)
        {
            using (var statement = Prepare(sql))
            {
                while (statement.Read())
                {
                }
            }
        }

        internal void Check(int code)
        {
            if (code == 0)
                return;
            string message = handle == IntPtr.Zero ? "Could not open the local database." :
                Marshal.PtrToStringUni(SqliteNative.sqlite3_errmsg16(handle));
            throw new IOException(message);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                SqliteNative.sqlite3_close(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    internal sealed class SqliteStatement : IDisposable
    {
        private readonly SqliteDatabase database;
        private IntPtr handle;

        public SqliteStatement(SqliteDatabase database, IntPtr handle)
        {
            this.database = database;
            this.handle = handle;
        }

        public void Bind(int column, long value)
        {
            database.Check(SqliteNative.sqlite3_bind_int64(handle, column, value));
        }

        public void Bind(int column, string value)
        {
            value = value ?? "";
            database.Check(SqliteNative.sqlite3_bind_text16(handle, column, value,
                value.Length * sizeof(char), new IntPtr(-1)));
        }

        public bool Read()
        {
            const int row = 100;
            const int done = 101;
            int code = SqliteNative.sqlite3_step(handle);
            if (code == row)
                return true;
            if (code == done)
                return false;
            database.Check(code);
            return false;
        }

        public long Number(int column)
        {
            return SqliteNative.sqlite3_column_int64(handle, column);
        }

        public string Text(int column)
        {
            IntPtr pointer = SqliteNative.sqlite3_column_text16(handle, column);
            if (pointer == IntPtr.Zero)
                return "";
            int bytes = SqliteNative.sqlite3_column_bytes16(handle, column);
            return Marshal.PtrToStringUni(pointer, bytes / sizeof(char));
        }

        public void Reset()
        {
            SqliteNative.sqlite3_reset(handle);
            SqliteNative.sqlite3_clear_bindings(handle);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                SqliteNative.sqlite3_finalize(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    internal static class SqliteNative
    {
        // Load only the SQLite library that ships in Windows System32.
        private const string Library = "winsqlite3.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int sqlite3_open16(string filename, out IntPtr database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg16(IntPtr database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int sqlite3_prepare16_v2(IntPtr database, string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text16(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes16(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_reset(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_clear_bindings(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);
    }
}
