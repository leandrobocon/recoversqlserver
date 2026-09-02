using System;
using System.Linq;

namespace OrcaSql.Core.Engine.Pages
{
	internal class RecordPage : Page
	{
		public short[] SlotArray { get; private set; }

		internal RecordPage(byte[] bytes, Database database)
			: base(bytes, database)
		{
			parseSlotArray();
		}

		private void parseSlotArray()
		{
			// Valor limite seguro: no maximo (8192-96)/2 slots possiveis numa pagina
			int slotCnt = Math.Min((int)Header.SlotCnt, 4000);
			SlotArray = new short[slotCnt];

			for (int i = 0; i < slotCnt; i++)
				SlotArray[i] = BitConverter.ToInt16(RawBytes.ToArray(), RawBytes.Count - i * 2 - 2);
		}
	}
}