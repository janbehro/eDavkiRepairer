namespace eDavkiRepairer;

internal class eDavkiRepairerOptions
{
    public string eDavkiBaseAddress { get; set; }
    public string RequestsDirectory { get; set; }
    public string PosDBFileName { get; set; }
    public string ResultPath { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string PosDirectory { get; set; }
}
