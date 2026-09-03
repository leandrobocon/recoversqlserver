using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using OrcaSql.Core.Engine.Pages;
using OrcaSql.Core.Engine.Pages.PFS;
using OrcaSql.Core.Engine.Records.Parsers;
using OrcaSql.Core.MetaData;
using OrcaSql.Core.MetaData.Enumerations;

namespace OrcaSql.Core.Engine
{
	public class DataScanner : Scanner
	{
		public DataScanner(Database database)
			: base(database)
		{ }

		/// <summary>
		/// Will scan any table - heap or clustered - and return an IEnumerable of generic rows with data & schema
		/// </summary>
		public IEnumerable<Row> ScanTable(string tableName, int? schemaId = null, bool isSysTable = true)
		{
			return ScanTable(tableName, schemaId, isSysTable, null);
		}

		/// <summary>
		/// Will scan any table - heap or clustered - and return an IEnumerable of generic rows with data & schema.
		/// Deferred columns are materialized on first read.
		/// </summary>
		public IEnumerable<Row> ScanTable(string tableName, int? schemaId, bool isSysTable,
			ISet<string> deferredColumns)
		{
			var schema = MetaData.GetEmptyDataRow(tableName, schemaId);

			return ScanTable(tableName, schema, isSysTable, deferredColumns);
		}

        public DataRow GetEmptyDataRow(string tableName, int? schemaId = null)
        {
            var schema = MetaData.GetEmptyDataRow(tableName, schemaId);

            return schema;
        }

        /// <summary>
        /// Will scan any table - heap or clustered - and return an IEnumerable of typed rows with data & schema
        /// </summary>
        internal IEnumerable<TDataRow> ScanTable<TDataRow>(string tableName) where TDataRow : Row, new()
		{
			var schema = new TDataRow();

			return ScanTable(tableName, schema).Cast<TDataRow>();
		}

		/// <summary>
		/// Scans a linked list of pages returning an IEnumerable of typed rows with data & schema
		/// </summary>
		internal IEnumerable<TDataRow> ScanLinkedDataPages<TDataRow>(PagePointer loc, CompressionContext compression) where TDataRow : Row, new()
		{
			return ScanLinkedDataPages(loc, new DataExtractorHelper(new TDataRow()), compression, true).Cast<TDataRow>();
		}

		/// <summary>
		/// Starts at the data page (loc) and follows the NextPage pointer chain till the end.
		/// </summary>
		internal IEnumerable<Row> ScanLinkedDataPages(PagePointer loc, DataExtractorHelper schema,
            CompressionContext compression, bool isSysTable)
		{
			var visited = new HashSet<long>();
			while (PagePointer.Zero != loc && loc != null && loc.PageID > 0)
			{
				if (!visited.Add(loc.PageID))
					break;

				PagePointer next;
				List<Row> rows = new List<Row>();
				try
				{
					var recordParser = RecordEntityParser.CreateEntityParserForPage(loc, compression, Database, isSysTable);
					next = recordParser.NextPage;
					rows.AddRange(recordParser.GetEntities(schema));
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine("AVISO: pagina " + loc + " na cadeia nao pode ser lida (corrupcao): " + ex.Message);
					next = TryReadNextPage(loc);
				}

				foreach (var dr in rows)
					yield return dr;

				loc = next;
			}
		}

		private PagePointer TryReadNextPage(PagePointer loc)
		{
			try
			{
				var bmField = Database.GetType().GetField("_bufferManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				var bm = bmField.GetValue(Database);
				var method = bm.GetType().GetMethod("GetPageBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				var bytes = (byte[])method.Invoke(bm, new object[] { loc.FileID, loc.PageID, false });
				return new PagePointer(System.BitConverter.ToInt16(bytes, 20), System.BitConverter.ToInt32(bytes, 16));
			}
			catch
			{
				return PagePointer.Zero;
			}
		}

		private IEnumerable<Row> ScanTable(string tableName, Row schema, bool isSysTable = true,
			ISet<string> deferredColumns = null)
		{
			// Get object
			var tableObject = Database.BaseTables.SysSchObjs
				.Where(x => x.name == tableName)
				.SingleOrDefault(x => x.type.Trim() == ObjectType.INTERNAL_TABLE || x.type.Trim() == ObjectType.SYSTEM_TABLE || x.type.Trim() == ObjectType.USER_TABLE);

			if (tableObject == null)
				throw new ArgumentException("Table does not exist.");

			// Get rowset, prefer clustered index if exists
			var partitions = Database.Dmvs.Partitions
				.Where(x => x.ObjectID == tableObject.id && x.IndexID <= 1)
				.OrderBy(x => x.PartitionNumber)
                .ToArray();

			if (!partitions.Any())
				throw new ArgumentException("Table has no partitions.");

			// Loop all partitions and return results one by one
			return partitions.SelectMany(partition => ScanPartition(partition.PartitionID, partition.PartitionNumber, schema, isSysTable, deferredColumns));
		}

		private IEnumerable<Row> ScanPartition(long partitionID, int partitionNumber, Row schema, bool isSysTable = true,
			ISet<string> deferredColumns = null)
		{
			// Lookup partition
			var partition = Database.Dmvs.Partitions
				.SingleOrDefault(p => p.PartitionID == partitionID && p.PartitionNumber == partitionNumber);

			if(partition == null)
				throw new ArgumentException("Partition (" + partitionID + "." + partitionNumber + " does not exist.");

			// Get allocation unit for in-row data
			var au = Database.Dmvs.SystemInternalsAllocationUnits
				.SingleOrDefault(x => x.ContainerID == partition.PartitionID && x.Type == 1);

			if (au == null)
				throw new ArgumentException("Partition (" + partition.PartitionID + "." + partition.PartitionNumber + " has no HOBT allocation unit.");

			// Before we can scan either heaps or indices, we need to know the compression level as that's set at the partition level, and not at the record/page level.
			// We also need to know whether the partition is using vardecimals.
			var compression = new CompressionContext((CompressionLevel)partition.DataCompression, MetaData.PartitionHasVardecimalColumns(partition.PartitionID));

            var clusteredIndex = isSysTable ? null : Database.Dmvs.Indexes.SingleOrDefault(x => x.ObjectID == partition.ObjectID && x.Type == 1);

            var useClusteredIndex = isSysTable || clusteredIndex != null;

            var partitionColumns = isSysTable ? null : Database.Dmvs.SystemInternalsPartitionColumns.Where(x => x.PartitionID == partition.PartitionID).ToArray();

            var defaultConstraints = isSysTable ? null : Database.Dmvs.SysDefaultConstraints.Where(x => x.ParentObjectId == partition.ObjectID).ToArray();

            var schemaWrapper = new DataExtractorHelper(schema, Database.Dmvs, null, partitionColumns, defaultConstraints, deferredColumns);

            // Heap tables won't have root pages, thus we can check whether a root page is defined for the HOBT allocation unit
            if (au.RootPagePointer != PagePointer.Zero && useClusteredIndex)
            {
                var currentPage = isSysTable ? au.FirstPagePointer : au.RootPagePointer;

                if (currentPage != au.FirstPagePointer)
                {
                    while (true)
                    {
                        var ciPage = Database.GetClusteredIndexPage(currentPage, isSysTable);

                        currentPage = ciPage.Records.Select(x => x.PageId).FirstOrDefault();

                        if (ciPage.Header.Level <= 1)
                        {
                            break;
                        }
                    }
                }

                // Index
                foreach (var row in ScanLinkedDataPages(currentPage, schemaWrapper, compression, isSysTable))
                    yield return row;
            }
            else
            {
				// Heap
				foreach (var row in ScanHeap(au.FirstIamPagePointer, schemaWrapper, compression, isSysTable))
					yield return row;
			}
		}

		public IEnumerable<string> PageLeafObjectIds()
		{
			var fs = Database.Files.First().Value;
			var totalBytes = fs.Length;
			var pageBytes = new byte[8192];
			var seen = new HashSet<int>();
			for (int pageId = 0; pageId < totalBytes / 8192; pageId++)
			{
				fs.Position = pageId * 8192L;
				if (fs.Read(pageBytes, 0, 8192) < 8192)
					break;
				if (pageBytes[0] != 1) continue;
				var type = pageBytes[1]; var level = pageBytes[3];
				if ((type != 1 && type != 2) || level != 0) continue;
				int oid = BitConverter.ToInt32(pageBytes, 24);
				if (oid <= 0 || seen.Contains(oid)) continue;
				seen.Add(oid);
				yield return oid.ToString();
			}
		}

		/// <summary>
		/// Escaneia fisicamente a contagem de slots (registros) por objectID fisico.
		/// </summary>
		public Dictionary<int, (long slots, int pages)> SlotCountsByObjectId()
		{
			var cache = BuildPageCache();
			var fs = Database.Files.First().Value;
			var pageBytes = new byte[8192];
			var res = new Dictionary<int, (long, int)>();
			foreach (var kv in cache)
			{
				long slots = 0;
				foreach (var pageId in kv.Value)
				{
					fs.Position = pageId * 8192L;
					if (fs.Read(pageBytes, 0, 8192) < 8192) continue;
					int slotCnt = BitConverter.ToInt16(pageBytes, 22);
					if (slotCnt > 0) slots += slotCnt;
				}
				res[kv.Key] = (slots, kv.Value.Count);
			}
			return res;
		}

		/// <summary>
		/// Mapeia tabela -> objectID fisico usando o log de contagens do Stellar como ground truth.
		/// Atribuicao gulosa por confianca: tabelas processadas em ordem crescente do desvio relativo
		/// frente ao melhor oid ainda livre, de forma que colisoes favorecem quem casa melhor.
		/// </summary>
		public Dictionary<string, int> AutoMapTables(IEnumerable<string> tableNames, Func<string, long> stellarCounts)
		{
			var slotCounts = SlotCountsByObjectId();
			var result = new Dictionary<string, int>();
			var usedOids = new HashSet<int>();

			var pending = tableNames
				.Select(name => (name, expect: stellarCounts(name)))
				.Where(t => t.expect > 0)
				.OrderByDescending(t => t.expect)
				.ToList();

			foreach (var t in pending) result[t.name] = 0;

			long Tol(long expect) => Math.Max(5, (long)(expect * 0.15));

			while (pending.Count > 0)
			{
				(string name, long expect) best = default;
				int bestOid = 0; double bestScore = double.MaxValue;
				for (int i = 0; i < pending.Count; i++)
				{
					var tp = pending[i];
					long tol = Tol(tp.expect);
					var cand = slotCounts
						.Where(kv => !usedOids.Contains(kv.Key))
						.Select(kv => (kv.Key, diff: Math.Abs((long)kv.Value.slots - tp.expect)))
						.Where(c => c.diff <= tol)
						.OrderBy(c => c.diff)
						.FirstOrDefault();
					if (cand.Key == 0) continue;
					double score = (double)cand.diff / tp.expect;
					if (score < bestScore || (score == bestScore && Math.Abs(cand.diff - bestScore * best.expect) == 0))
					{
						if (best.name == null || score < bestScore)
						{
							bestScore = score;
							best = tp;
							bestOid = cand.Key;
						}
					}
				}

				if (best.name == null) break; /* nenhum candidato restante dentro da tolerancia */

				result[best.name] = bestOid;
				usedOids.Add(bestOid);
				var sc = slotCounts[bestOid];
				Console.WriteLine($"[AutoMap] {best.name}: Stellar={best.expect} -> oid {bestOid} (slots={sc.slots}, pag={sc.pages})");
				for (int i = 0; i < pending.Count; i++)
				if (pending[i].name == best.name) { pending.RemoveAt(i); break; }
			}

			foreach (var p in pending)
				Console.WriteLine($"[AutoMap] {p.name}: Stellar={p.expect} -> oid 0 (sem candidato dentro da tolerancia)");
			return result;
		}

		/// <summary>
		/// Escaneia fisicamente todas as paginas de dados do arquivo (fileID 1) associadas ao objectID, sem depender do
		/// catalogo sysrowsets (partitions). Usado quando o catalogo esta corrompido.
		/// </summary>
		public IEnumerable<Row> ScanTableByObjectId(string tableName, int objectId, bool isSysTable = false)
		{
			var schema = MetaData.GetEmptyDataRow(tableName, null);
			var schemaWrapper = new DataExtractorHelper(schema);
			var compression = new CompressionContext(CompressionLevel.None, false);

			var pageIds = GetPageIdsForObjectId(objectId);
			var fs = Database.Files.First().Value;
			var pageBytes = new byte[8192];

			foreach (var pageId in pageIds)
			{
				fs.Position = pageId * 8192L;
				if (fs.Read(pageBytes, 0, 8192) < 8192)
					continue;

				var parsedRows = new List<Row>();
				try
				{
					var copy = (byte[])pageBytes.Clone();
					var page = new PrimaryRecordPage(copy, compression, Database);
					var parser = new PrimaryRecordEntityParser(page, compression);
					parsedRows.AddRange(parser.GetEntities(schemaWrapper));
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine("AVISO: pagina " + pageId + " nao pode ser parseada (corrupcao): " + ex.Message);
				}
				foreach (var row in parsedRows)
					yield return row;
			}
		}

		private Dictionary<int, List<long>> _pageCache;
		private Dictionary<int, List<long>> BuildPageCache()
		{
			if (_pageCache != null) return _pageCache;
			_pageCache = new Dictionary<int, List<long>>();
			var fs = Database.Files.First().Value;
			var totalBytes = fs.Length;
			var pageBytes = new byte[8192];
			for (int pageId = 0; pageId < totalBytes / 8192; pageId++)
			{
				fs.Position = pageId * 8192L;
				if (fs.Read(pageBytes, 0, 8192) < 8192) break;
				if (pageBytes[0] != 1) continue;
				var type = pageBytes[1]; var level = pageBytes[3];
				if ((type != 1 && type != 2) || level != 0) continue;
				int oid = BitConverter.ToInt32(pageBytes, 24);
				if (oid <= 0) continue;
				if (!_pageCache.ContainsKey(oid)) _pageCache[oid] = new List<long>();
				_pageCache[oid].Add(pageId);
			}
			return _pageCache;
		}

		private List<long> GetPageIdsForObjectId(int objectId)
		{
			var cache = BuildPageCache();
			return cache.ContainsKey(objectId) ? cache[objectId] : new List<long>();
		}
		/// <summary>
		/// Escaneia fisicamente todas as paginas de dados do arquivo (fileID 1) associadas ao objectID, sem depender do
		/// catalogo sysrowsets (partitions). Usado quando o catalogo esta corrompido.
		/// </summary>
		private IEnumerable<Row> ScanHeap(PagePointer loc, DataExtractorHelper schema, CompressionContext compression,
            bool isSysTable)
		{
			// Traverse the linked list of IAM pages until the tail pointer is zero
			while (loc != PagePointer.Zero)
			{
				// Before scanning, check that the IAM page itself is allocated
				var pfsPage = Database.GetPfsPage(PfsPage.GetPfsPointerForPage(loc));

				// If IAM page isn't allocated, there's nothing to return
				if (!pfsPage.GetPageDescription(loc.PageID).IsAllocated)
					yield break;

				var iamPage = Database.GetIamPage(loc, isSysTable);

				// Create an array with all of the header slot pointers
				var iamPageSlots = new []
					{
						iamPage.Slot0,
						iamPage.Slot1,
						iamPage.Slot2,
						iamPage.Slot3,
						iamPage.Slot4,
						iamPage.Slot5,
						iamPage.Slot6,
						iamPage.Slot7
					};

				// Loop each header slot and yield the results, provided the header slot is allocated
				foreach (var slot in iamPageSlots.Where(x => x != PagePointer.Zero))
				{
					var recordParser = RecordEntityParser.CreateEntityParserForPage(slot, compression, Database, isSysTable);

					foreach (var dr in recordParser.GetEntities(schema))
						yield return dr;
				}

				// Then loop through allocated extents and yield results
				foreach (var extent in iamPage.GetAllocatedExtents())
				{
					// Get PFS page that tracks this extent
					var pfs = Database.GetPfsPage(PfsPage.GetPfsPointerForPage(extent.StartPage));
					
					foreach (var pageLoc in extent.GetPagePointers())
					{
						// Check if page is allocated according to PFS page
						var pfsDescription = pfs.GetPageDescription(pageLoc.PageID);

						if(!pfsDescription.IsAllocated)
							continue;

						var recordParser = RecordEntityParser.CreateEntityParserForPage(pageLoc, compression, Database, isSysTable);

						foreach (var dr in recordParser.GetEntities(schema))
							yield return dr;
					}
				}

				// Update current IAM chain location to the tail pointer
				loc = iamPage.Header.NextPage;
			}
		}
    }
}
