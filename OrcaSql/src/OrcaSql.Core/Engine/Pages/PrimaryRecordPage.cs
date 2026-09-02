using System;
using System.Linq;
using OrcaSql.Core.Engine.Records;
using OrcaSql.Framework;

namespace OrcaSql.Core.Engine.Pages
{
	internal class PrimaryRecordPage : RecordPage
	{
		internal PrimaryRecord[] Records { get; set; }

		protected CompressionContext CompressionContext;

		internal PrimaryRecordPage(byte[] bytes, CompressionContext compression, Database database)
			: base(bytes, database)
		{
			CompressionContext = compression;

			parseRecords();
		}

		private void parseRecords()
		{
			try
			{
				var records = new PrimaryRecord[Header.SlotCnt];
				int cnt = 0;
				foreach (short recordOffset in SlotArray)
				{
					if (recordOffset < 0 || recordOffset >= RawBytes.Count)
					{
						System.Diagnostics.Debug.WriteLine("AVISO: slot offset invalido (" + recordOffset + ") - ignorando registro corrompido.");
						continue;
					}
					records[cnt++] = new PrimaryRecord(ArrayHelper.SliceArray(RawBytes.ToArray(), recordOffset, RawBytes.Count - recordOffset), this);
				}
				Records = records;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("AVISO: falha ao parsear records desta pagina (corrupcao): " + ex.Message);
				Records = new PrimaryRecord[0];
			}
		}
	}
}