namespace PaxCounterWeb.Models

{
    namespace PaxCounterWeb.Models
    {
        public class RssiSample
        {
            public int Id { get; set; }

            public int PaxSampleId { get; set; }
            public PaxSample PaxSample { get; set; } = null!;

            public int Rssi { get; set; }
            public int Count { get; set; }
        }

    }
}
