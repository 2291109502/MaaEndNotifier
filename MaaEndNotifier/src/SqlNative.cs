using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MaaEndNotify {
    // One owner per connection. Every statement is finalized before the caller regains control.
    internal sealed class SqlDb : IDisposable {
        const string Library = "winsqlite3.dll";
        const CallingConvention Abi = CallingConvention.StdCall;
        const int Row = 100, Done = 101, SqlNull = 5;
        static readonly IntPtr Transient = new IntPtr(-1);
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        IntPtr db;

        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_open_v2(byte[] path, out IntPtr handle, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_close(IntPtr handle);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_busy_timeout(IntPtr handle, int ms);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_prepare_v2(IntPtr handle, IntPtr sql, int bytes, out IntPtr stmt, out IntPtr tail);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_bind_parameter_count(IntPtr stmt);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_bind_null(IntPtr stmt, int index);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_column_type(IntPtr stmt, int column);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_column_bytes(IntPtr stmt, int column);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Library, CallingConvention=Abi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int sqlite3_changes(IntPtr handle);

        public SqlDb(string absolutePath) {
            var path = Path.GetFullPath(absolutePath);
            if (!Path.IsPathRooted(absolutePath)) throw new ArgumentException("通知数据库需要绝对路径。");
            try {
                Check(sqlite3_open_v2(Utf8.GetBytes(path + "\0"), out db, 2 | 4, IntPtr.Zero));
                Check(sqlite3_busy_timeout(db, 5000));
            } catch { Dispose(); throw; }
        }

        static void Check(int result) {
            if (result == 0) return;
            // Do not expose SQL or a value substituted into SQLite's diagnostic message.
            string reason;
            switch (result & 255) {
                case 5: case 6: reason = "数据库正忙"; break;
                case 8: reason = "数据库为只读"; break;
                case 10: reason = "读写失败"; break;
                case 11: case 26: reason = "数据库格式错误或文件损坏"; break;
                case 13: reason = "磁盘空间不足"; break;
                case 14: reason = "无法打开数据库文件"; break;
                case 19: reason = "数据约束检查失败"; break;
                default: reason = "操作失败"; break;
            }
            throw new IOException("通知数据库" + reason + "（SQLite " + result.ToString(CultureInfo.InvariantCulture) + "）。");
        }

        public int Exec(string sql, params object[] args) { return Execute(sql, args, null); }

        public List<string[]> Rows(string sql, params object[] args) {
            var rows = new List<string[]>();
            Execute(sql, args, rows);
            return rows;
        }

        int Execute(string sql, object[] args, List<string[]> rows) {
            if (db == IntPtr.Zero) throw new ObjectDisposedException("SqlDb");
            if (String.IsNullOrWhiteSpace(sql) || sql.IndexOf('\0') >= 0) throw new ArgumentException("无效的数据库语句。");
            var buffer = Utf8.GetBytes(sql + "\0");
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var stmt = IntPtr.Zero;
            try {
                IntPtr tail;
                var start = pinned.AddrOfPinnedObject();
                Check(sqlite3_prepare_v2(db, start, buffer.Length, out stmt, out tail));
                int used = checked((int)(tail.ToInt64() - start.ToInt64()));
                if (stmt == IntPtr.Zero || (used < buffer.Length-1 &&
                    !String.IsNullOrWhiteSpace(Utf8.GetString(buffer, used, buffer.Length - 1 - used))))
                    throw new ArgumentException("每次数据库调用需要一条语句。");
                if (args == null || sqlite3_bind_parameter_count(stmt) != args.Length)
                    throw new ArgumentException("数据库参数数量不匹配。");
                for (int i = 0; i < args.Length; i++) Bind(stmt, i + 1, args[i]);
                int result;
                while ((result = sqlite3_step(stmt)) == Row) {
                    if (rows == null) continue;
                    var row = new string[sqlite3_column_count(stmt)];
                    for (int i = 0; i < row.Length; i++) {
                        if (sqlite3_column_type(stmt,i) == SqlNull) continue;
                        IntPtr ptr = sqlite3_column_text(stmt,i);
                        if (ptr == IntPtr.Zero) throw new OutOfMemoryException();
                        var data = new byte[sqlite3_column_bytes(stmt,i)];
                        if (data.Length != 0) Marshal.Copy(ptr, data, 0, data.Length);
                        row[i] = Utf8.GetString(data);
                    }
                    rows.Add(row);
                }
                if (result != Done) Check(result);
                return sqlite3_changes(db);
            } finally {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                pinned.Free();
            }
        }

        static void Bind(IntPtr stmt, int index, object value) {
            if (value == null || value == DBNull.Value) Check(sqlite3_bind_null(stmt,index));
            else if (value is int || value is long) Check(sqlite3_bind_int64(stmt,index,Convert.ToInt64(value,CultureInfo.InvariantCulture)));
            else if (value is string) {
                // A non-null buffer is needed even for empty strings. SQLite copies the exact byte count.
                var bytes = Utf8.GetBytes((string)value + "\0");
                Check(sqlite3_bind_text(stmt,index,bytes,bytes.Length-1,Transient));
            } else throw new ArgumentException("不支持的数据库参数类型。");
        }

        public void Dispose() {
            if (db == IntPtr.Zero) return;
            sqlite3_close(db);
            db = IntPtr.Zero;
        }
    }
}
