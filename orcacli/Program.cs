using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OrcaSql.Core.Engine;
using OrcaSql.Core.MetaData;

class Program
{
    static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Length < 2)
        {
            Console.WriteLine("Uso: orcacli <arquivo.mdf> <pasta_saida> [somenteTabela]");
            return 2;
        }
        var mdf = args[0];
        var outDir = args[1];
        string onlyTable = args.Length > 2 ? args[2] : null;

        if (!File.Exists(mdf)) { Console.WriteLine("MDF não encontrado: " + mdf); return 1; }
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"Abrindo {mdf} ...");
        Database db;
        try
        {
            db = new Database(mdf);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERRO ao abrir banco: " + ex.Message);
            Console.WriteLine(ex.ToString());
            return 1;
        }
        Console.WriteLine($"Banco: {db.Name}");

        var scanner = new DataScanner(db);
        var tables = db.Dmvs.Tables.Where(t => !t.IsMSShipped).OrderBy(t => t.Name).ToList();
        Console.WriteLine($"Tabelas de usuário encontradas: {tables.Count}");
        var withPart = tables.Count(t => db.Dmvs.Partitions.Any(p => p.ObjectID == t.ObjectID && p.IndexID <= 1));
        Console.WriteLine($"Com partition: {withPart} / Sem: {tables.Count - withPart}");

        if (Environment.GetEnvironmentVariable("DIAGOIDS") == "1")
        {
            var counts = new Dictionary<int, int>();
            var filesField = db.GetType().GetField("Files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var filesDict = (System.Collections.Generic.Dictionary<short, System.IO.Stream>)filesField.GetValue(db);
            var fso = filesDict.First().Value;
            var pageBytes = new byte[8192];
            var oidSet = new HashSet<int>();
            for (int pid = 0; pid < fso.Length / 8192; pid++)
            {
                fso.Position = pid * 8192L;
                fso.Read(pageBytes, 0, 8192);
                if (pageBytes[0] != 1) continue;
                var type = pageBytes[1]; var level = pageBytes[3];
                if ((type != 1 && type != 2) || level != 0) continue;
                int oid = BitConverter.ToInt32(pageBytes, 24);
                if (oid > 0)
                {
                    counts.TryGetValue(oid, out var c); counts[oid] = c + 1;
                    oidSet.Add(oid);
                }
            }
            Console.WriteLine("Total objectIDs físicos distintos: " + oidSet.Count);
            Console.WriteLine("Top objectIDs físicos (com nº de páginas):");
            foreach (var kv in counts.OrderByDescending(x => x.Value).Take(60))
                Console.WriteLine($"  oid {kv.Key}: {kv.Value} paginas");
            int matched = 0;
            foreach (var t in tables)
            {
                if (oidSet.Contains(t.ObjectID)) matched++;
            }
            Console.WriteLine($"Catalog IDs presentes no físico: {matched}/{tables.Count}");
            var suffix = args.Length > 2 ? args[2] : "";
            foreach (var t in tables.Where(x => string.IsNullOrEmpty(suffix) || x.Name.ToLower().Contains(suffix)))
                Console.WriteLine($"  {t.Name}\tcat={t.ObjectID}\tfisico?={oidSet.Contains(t.ObjectID)}");
            return 0;
        }

        if (Environment.GetEnvironmentVariable("DIAGSCAN") == "1")
        {
            var tableName2 = args.Length > 2 ? args[2] : "GST020";
            var tbl2 = tables.First(t => t.Name == tableName2);
            Console.WriteLine($"Tabela {tableName2}: cat_ObjectID={tbl2.ObjectID}");
            var partitions2 = db.Dmvs.Partitions.Where(p => p.ObjectID == tbl2.ObjectID && p.IndexID <= 1).ToArray();
            Console.WriteLine($"partitions: {partitions2.Length}");
            foreach (var p in partitions2)
                Console.WriteLine($"  PartitionID={p.PartitionID} Num={p.PartitionNumber} IndexID={p.IndexID}");
            var au = db.Dmvs.SystemInternalsAllocationUnits.FirstOrDefault(a => a.ContainerID == partitions2.First().PartitionID && a.Type == 1);
            Console.WriteLine($"AU: firstIam={au?.FirstIamPagePointer} firstPg={au?.FirstPagePointer} root={au?.RootPagePointer}");
            var filesField2 = db.GetType().GetField("Files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var filesDict2 = (System.Collections.Generic.Dictionary<short, System.IO.Stream>)filesField2.GetValue(db);
            var fso2 = filesDict2.First().Value;
            var pb2 = new byte[8192];
            if (au != null && au.FirstIamPagePointer != null && au.FirstIamPagePointer.PageID > 0 && au.FirstIamPagePointer.FileID == 1)
            {
                fso2.Position = au.FirstIamPagePointer.PageID * 8192L;
                fso2.Read(pb2, 0, 8192);
                Console.WriteLine($"IAM header: type={pb2[1]} level={pb2[3]} m_objId={BitConverter.ToInt32(pb2, 24)} m_indexId={BitConverter.ToInt16(pb2, 6)} next=" + BitConverter.ToInt32(pb2, 16));
            }
            if (au != null && au.FirstPagePointer != null && au.FirstPagePointer.PageID > 0 && au.FirstPagePointer.FileID == 1)
            {
                fso2.Position = au.FirstPagePointer.PageID * 8192L;
                fso2.Read(pb2, 0, 8192);
                Console.WriteLine($"FirstPg header: type={pb2[1]} level={pb2[3]} m_objId={BitConverter.ToInt32(pb2, 24)} m_indexId={BitConverter.ToInt16(pb2, 6)} slotCnt={BitConverter.ToInt16(pb2, 22)}");
                Console.WriteLine("HEX first 96:");
                Console.WriteLine(BitConverter.ToString(pb2, 0, 96));
                for (int off = 0; off < 96; off += 4)
                    Console.Write($"  {off}:{BitConverter.ToInt32(pb2, off)}(0x{BitConverter.ToInt32(pb2, off):x8})");
                Console.WriteLine();
            }
            return 0;
        }

        if (Environment.GetEnvironmentVariable("DIAGPHYSOID") == "1")
        {
            var tn = args.Length > 2 ? args[2] : "GST020";
            int poid = int.Parse(Environment.GetEnvironmentVariable("PHYS_OID") ?? "593");
            var rows2 = scanner.ScanTableByObjectId(tn, poid, false).ToList();
            Console.WriteLine($"Tabela {tn} via oid físico {poid}: {rows2.Count} linhas");
            if (rows2.Count > 0)
            {
                var cols = rows2[0].Columns.Select(c => c.Name).ToArray();
                Console.WriteLine("Colunas: " + string.Join(", ", cols.Take(30)));
                foreach (var r in rows2.Take(5))
                    Console.WriteLine("  " + string.Join(" | ", cols.Take(8).Select(c => (r.GetRawValue(c) ?? "").ToString())));
            }
            return 0;
        }

        if (Environment.GetEnvironmentVariable("DIAGSLOT") == "1")
        {
            var filesField6 = db.GetType().GetField("Files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var filesDict6 = (System.Collections.Generic.Dictionary<short, System.IO.Stream>)filesField6.GetValue(db);
            var fso6 = filesDict6.First().Value;
            var pb6 = new byte[8192];
            var slotsByOid6 = new Dictionary<int, (long slots, int pages)>();
            for (int pid = 0; pid < fso6.Length / 8192; pid++)
            {
                fso6.Position = pid * 8192L;
                if (fso6.Read(pb6, 0, 8192) < 8192) break;
                if (pb6[0] != 1) continue;
                var type = pb6[1]; var level = pb6[3];
                if ((type != 1 && type != 2) || level != 0) continue;
                int oid = BitConverter.ToInt32(pb6, 24);
                int slotCnt = BitConverter.ToInt16(pb6, 22);
                if (oid <= 0 || slotCnt <= 0) continue;
                if (!slotsByOid6.ContainsKey(oid)) slotsByOid6[oid] = (0, 0);
                slotsByOid6[oid] = (slotsByOid6[oid].slots + slotCnt, slotsByOid6[oid].pages + 1);
            }
            var tbl6 = tables.First(t => t.Name == args[2]);
            long expect6 = 0;
            var logPath6 = Environment.GetEnvironmentVariable("STELLAR_LOG") ?? (args[1] ?? "") + ".log";
            foreach (var line in File.ReadAllLines(logPath6, Encoding.Unicode))
            {
                var m6 = System.Text.RegularExpressions.Regex.Match(line, @"^dbo\.(\w+)\s*:\s*(\d+)\s*Records");
                if (m6.Success && m6.Groups[1].Value.ToLower() == (args[2] ?? "").ToLower()) expect6 = long.Parse(m6.Groups[2].Value);
            }
            Console.WriteLine($"Tabela {tbl6.Name}: Stellar={expect6} (chema {tbl6.SchemaID})");
            Console.WriteLine("Top oids por slots:");
            foreach (var kv in slotsByOid6.OrderByDescending(k => k.Value.slots).Take(20))
                Console.WriteLine($"  oid {kv.Key}: slots={kv.Value.slots} paginas={kv.Value.pages}");
            foreach (var kv in slotsByOid6.OrderBy(k => Math.Abs((long)k.Value.slots - expect6)).Take(8))
                Console.WriteLine($"  ~oid {kv.Key}: slots={kv.Value.slots} paginas={kv.Value.pages}");
            return 0;
        }

        if (Environment.GetEnvironmentVariable("AUTOMAP") == "1")
        {
            var targetTbl = (args.Length > 2 ? args[2] : null)?.ToLower();
            var candidates = targetTbl != null
                ? tables.Where(x => x.Name.ToLower().Contains(targetTbl)).ToList()
                : tables.ToList();
            Console.WriteLine($"Candidatos a mapear: {candidates.Count} tabelas");

            var stellar = new Dictionary<string, long>();
            var logPath = Environment.GetEnvironmentVariable("STELLAR_LOG") ?? (args[1] ?? "") + ".log";
            if (File.Exists(logPath))
            {
                foreach (var line in File.ReadAllLines(logPath, Encoding.Unicode))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^dbo\.(\w+)\s*:\s*(\d+)\s*Records");
                    if (m.Success) stellar[m.Groups[1].Value.ToLower()] = long.Parse(m.Groups[2].Value);
                }
                Console.WriteLine("Log Stellar lido: " + stellar.Count + " tabelas");
            }

            long Stellar(string name) => stellar.ContainsKey(name.ToLower()) ? stellar[name.ToLower()] : -1;

            var map = scanner.AutoMapTables(candidates.Select(t => t.Name), Stellar);

            File.WriteAllLines("/tmp/opencode/mapa_oids.tsv",
                map.Select(kv => $"{kv.Key}\t{kv.Value}"));
            return 0;
        }

        if (Environment.GetEnvironmentVariable("EXPORTPHYS") == "1")
        {
            if (!File.Exists("/tmp/opencode/mapa_oids.tsv"))
            {
                Console.WriteLine("Mapa inexistente; rode AUTOMAP=1 antes.");
                return 1;
            }
            var mapa = File.ReadLines("/tmp/opencode/mapa_oids.tsv")
                .Select(l => l.Split('\t'))
                .Where(a => a.Length == 2)
                .ToDictionary(a => a[0], a => int.Parse(a[1]));
            var summaryP = new StringBuilder();
            summaryP.AppendLine($"Banco: {db.Name}");
            summaryP.AppendLine($"MDF: {mdf}");
            summaryP.AppendLine($"Tabelas: {tables.Count}");
            summaryP.AppendLine();
            int okP = 0, failP = 0, totalRowsP = 0;
            foreach (var t in tables)
            {
                if (onlyTable != null && !string.Equals(t.Name, onlyTable, StringComparison.OrdinalIgnoreCase))
                    continue;
                int oid = mapa.ContainsKey(t.Name) ? mapa[t.Name] : 0;
                if (oid == 0)
                {
                    Console.Write($"\n[{t.Name}] (cat {t.ObjectID}, oid 0) ... ");
                    Console.WriteLine("sem mapeamento - pulando");
                    summaryP.AppendLine($"{t.Name}\t0\tSEM_MAPA");
                    continue;
                }
                Console.Write($"\n[{t.Name}] (cat {t.ObjectID}, oid físico {oid}) ... ");
                try
                {
                    List<Row> rows;
                    rows = scanner.ScanTableByObjectId(t.Name, oid, false).ToList();
                    var cols = rows.Count > 0 ? rows[0].Columns : GetSchemaCols(scanner, t);
                    var safeName = Sanitize(t.Name);
                    ExportCsv(Path.Combine(outDir, safeName + ".csv"), rows, cols);
                    ExportInsertSql(Path.Combine(outDir, safeName + "_insert.sql"), t.Name, rows, cols);
                    summaryP.AppendLine($"{t.Name}\t{rows.Count}\tOK");
                    totalRowsP += rows.Count;
                    okP++;
                    Console.WriteLine($"{rows.Count} linhas exportadas");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FALHOU: " + ex.Message);
                    if (Environment.GetEnvironmentVariable("DETAIL") == "1")
                        Console.WriteLine(ex.ToString());
                    summaryP.AppendLine($"{t.Name}\t0\tFALHOU\t{ex.Message}");
                    failP++;
                }
            }
            summaryP.AppendLine();
            summaryP.AppendLine($"OK={okP} FALHOU={failP} TOTAL_LINHAS={totalRowsP}");
            File.WriteAllText(Path.Combine(outDir, "_resumo_phys.tsv"), summaryP.ToString());
            Console.WriteLine($"\nConcluído. OK={okP} FALHOU={failP} linhas={totalRowsP}. Saída em {outDir}");
            return 0;
        }

        if (Environment.GetEnvironmentVariable("DIAGIAM") == "1")
        {
            var filesField3 = db.GetType().GetField("Files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var filesDict3 = (System.Collections.Generic.Dictionary<short, System.IO.Stream>)filesField3.GetValue(db);
            var fso3 = filesDict3.First().Value;
            var pb3 = new byte[8192];
            var iamByOid = new Dictionary<int, List<long>>();
            var iamByIdx = new Dictionary<(int oid, int idx), int>();
            for (int pid = 0; pid < fso3.Length / 8192; pid++)
            {
                fso3.Position = pid * 8192L;
                fso3.Read(pb3, 0, 8192);
                if (pb3[0] != 1) continue;
                if (pb3[1] != 10) continue;
                int oid = BitConverter.ToInt32(pb3, 24);
                short idx = BitConverter.ToInt16(pb3, 6);
                if (!iamByOid.ContainsKey(oid)) iamByOid[oid] = new List<long>();
                iamByOid[oid].Add(pid);
                var k = (oid, (int)idx);
                if (!iamByIdx.ContainsKey(k)) iamByIdx[k] = 0;
                iamByIdx[k]++;
            }
            Console.WriteLine("IAMs por objectID (distinct oids: " + iamByOid.Count + "):");
            foreach (var kv in iamByOid.OrderByDescending(x => x.Value.Count))
                Console.WriteLine($"  oid {kv.Key}: {kv.Value.Count} IAMs, ex.: {string.Join(",", kv.Value.Take(3))}");
            Console.WriteLine("IAMs por (oid,indexId):");
            foreach (var kv in iamByIdx.OrderByDescending(x => x.Value).Take(40))
                Console.WriteLine($"  ({kv.Key.oid}, idx {kv.Key.idx}): {kv.Value}");
            return 0;
        }

        if (Environment.GetEnvironmentVariable("DIAGCOL") == "1")
        {
            var filesField = db.GetType().GetField("Files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var filesDict = (System.Collections.Generic.Dictionary<short, System.IO.Stream>)filesField.GetValue(db);
            var fso = filesDict.First().Value;
            var pageBytes = new byte[8192];
            var oidSet = new HashSet<int>();
            for (int pid = 0; pid < fso.Length / 8192; pid++)
            {
                fso.Position = pid * 8192L;
                fso.Read(pageBytes, 0, 8192);
                if (pageBytes[0] != 1) continue;
                var type = pageBytes[1]; var level = pageBytes[3];
                if ((type != 1 && type != 2) || level != 0) continue;
                int oid = BitConverter.ToInt32(pageBytes, 24);
                if (oid > 0) oidSet.Add(oid);
            }
            var colpar = db.BaseTables.SysColPars.Select(c => c.id).Distinct().ToList();
            Console.WriteLine("Distinct syscolpar.id: " + colpar.Count);
            int colMatch = colpar.Count(id => oidSet.Contains(id));
            Console.WriteLine("syscolpar.id presentes no físico: " + colMatch + "/" + colpar.Count);
            var suffix2 = args.Length > 2 ? args[2] : "";
            foreach (var tbl in tables.Select((t, i) => new { t, i }).Where(x => string.IsNullOrEmpty(suffix2) || x.t.Name.ToLower().Contains(suffix2)))
            {
                var colsOfTable = db.BaseTables.SysColPars.Where(c => c.id == tbl.t.ObjectID).ToList();
                Console.WriteLine($"  {tbl.t.Name}\tcat={tbl.t.ObjectID}\tcols={colsOfTable.Count}\tfid={string.Join(",", colsOfTable.Select(c => c.id).Distinct())}");
            }
            return 0;
        }

        var summary = new StringBuilder();
        summary.AppendLine($"Banco: {db.Name}");
        summary.AppendLine($"MDF: {mdf}");
        summary.AppendLine($"Tabelas: {tables.Count}");
        summary.AppendLine();

        int ok = 0, fail = 0, totalRows = 0;
        foreach (var t in tables)
        {
            if (onlyTable != null && !string.Equals(t.Name, onlyTable, StringComparison.OrdinalIgnoreCase))
                continue;

            Console.Write($"\n[{t.Name}] (obj {t.ObjectID}, schema {t.SchemaID}) ... ");
            try
            {
                List<Row> rows;
                try
                {
                    rows = scanner.ScanTable(t.Name, t.SchemaID, false).ToList();
                }
                catch (ArgumentException) when (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USEFALLBACK")))
                {
                    Console.Write("(fallback físico) ");
                    var overrideOid = Environment.GetEnvironmentVariable("PHYS_OID");
                    rows = scanner.ScanTableByObjectId(t.Name, overrideOid != null ? int.Parse(overrideOid) : t.ObjectID, false).ToList();
                }
                var cols = rows.Count > 0 ? rows[0].Columns : GetSchemaCols(scanner, t);
                var safeName = Sanitize(t.Name);
                ExportCsv(Path.Combine(outDir, safeName + ".csv"), rows, cols);
                ExportInsertSql(Path.Combine(outDir, safeName + "_insert.sql"), t.Name, rows, cols);
                summary.AppendLine($"{t.Name}\t{rows.Count}\tOK");
                totalRows += rows.Count;
                ok++;
                Console.WriteLine($"{rows.Count} linhas exportadas");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FALHOU: " + ex.Message);
                if (Environment.GetEnvironmentVariable("DETAIL") == "1")
                    Console.WriteLine(ex.ToString());
                summary.AppendLine($"{t.Name}\t0\tFALHOU\t{ex.Message}");
                fail++;
            }
        }

        summary.AppendLine();
        summary.AppendLine($"OK={ok} FALHOU={fail} TOTAL_LINHAS={totalRows}");
        File.WriteAllText(Path.Combine(outDir, "_resumo.tsv"), summary.ToString());

        Console.WriteLine($"\nConcluído. OK={ok} FALHOU={fail} linhas={totalRows}. Saída em {outDir}");
        return 0;
    }

    static System.Collections.ObjectModel.ReadOnlyCollection<DataColumn> GetSchemaCols(DataScanner scanner, OrcaSql.Core.MetaData.DMVs.Table t)
    {
        try
        {
            var r = scanner.GetEmptyDataRow(t.Name, t.SchemaID);
            return r.Columns;
        }
        catch { return new System.Collections.ObjectModel.ReadOnlyCollection<DataColumn>(new List<DataColumn>()); }
    }

    static string Sanitize(string n)
    {
        var inv = Path.GetInvalidFileNameChars();
        return new string(n.Select(c => inv.Contains(c) ? '_' : c).ToArray());
    }

    static string CsvEscape(object v)
    {
        if (v == null || v == DBNull.Value) return "";
        var s = v.ToString();
        if (v is DateTime) s = ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss.fff");
        var sb = new StringBuilder();
        sb.Append('"');
        sb.Append(s.Replace("\"", "\"\""));
        sb.Append('"');
        return sb.ToString();
    }

    static object Val(Row row, DataColumn col)
    {
        try { return row.GetRawValue(col.Name); } catch { return null; }
    }

    static void ExportCsv(string path, List<Row> rows, System.Collections.ObjectModel.ReadOnlyCollection<DataColumn> cols)
    {
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            if (cols != null)
                w.WriteLine(string.Join(",", cols.Select(c => "\"" + c.Name + "\"")));
            foreach (var row in rows)
            {
                var fields = row.Columns.Select(c => CsvEscape(Val(row, c)));
                w.WriteLine(string.Join(",", fields));
            }
        }
    }

    static string SqlLiteral(object v)
    {
        if (v == null || v == DBNull.Value) return "NULL";
        if (v is bool) return ((bool)v) ? "1" : "0";
        if (v is byte[]) return "0x" + BitConverter.ToString((byte[])v).Replace("-", "");
        if (v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint ||
            v is long || v is ulong || v is float || v is double || v is decimal)
            return v.ToString().Replace(',', '.');
        var s = v.ToString().Replace("'", "''");
        return "N'" + s + "'";
    }

    static void ExportInsertSql(string path, string tableName, List<Row> rows, System.Collections.ObjectModel.ReadOnlyCollection<DataColumn> cols)
    {
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            w.WriteLine("-- INSERTs para " + tableName + " (" + rows.Count + " linhas)");
            w.WriteLine("-- Gerado por OrcaSql via orcacli");
            if (cols == null || cols.Count == 0) { w.WriteLine("-- sem colunas"); return; }
            w.WriteLine("INSERT INTO [" + tableName + "] ([" + string.Join("], [", cols.Select(c => c.Name)) + "]) VALUES");
            var lines = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                var vals = string.Join(", ", cols.Select(c => SqlLiteral(Val(rows[i], c))));
                lines.Add("  (" + vals + ")");
            }
            w.WriteLine(string.Join(",\n", lines) + ";");
        }
    }
}
