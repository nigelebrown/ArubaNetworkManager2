using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Dependency-free CSV, Office Open XML .xlsx and basic paginated PDF exports.
/// Every output is generated from the same variance rows; unavailable/uncertified
/// observed values remain explicitly labelled as such.
/// </summary>
public static class VarianceExportService
{
    private static readonly string[] Columns={
        "Device","Model","Site","Setting","Global","Site override","Device override",
        "Optimization","Effective desired","Actual/Imported","Source G/S/D/O/I",
        "Indicator G/S/D/O/I/!/P","Evidence quality","Observed UTC"
    };
    private static string[] Cells(ConfigurationVarianceService.Row r)=>new[]{
        r.Device,r.Model,r.Site??"",r.Setting,r.Global,r.SiteValue,r.DeviceValue,
        r.Optimization,r.Effective,r.Actual,r.Source,r.Indicator,r.Quality,r.EvidenceUtc
    };
    static string CsvEscape(string value)
    {
        // Neutralize Excel/LibreOffice CSV formula injection, including leading whitespace.
        var text=value??"";
        if(text.TrimStart().Length>0 && "=+-@\t\r".Contains(text.TrimStart()[0]))
            text="'"+text;
        return "\""+text.Replace("\"","\"\"")+"\"";
    }
    public static byte[] Csv(ConfigurationVarianceService.Report report)
    {
        var sb=new StringBuilder();
        sb.AppendLine(string.Join(",",Columns.Select(CsvEscape)));
        foreach(var row in report.Rows.Take(5000))
            sb.AppendLine(string.Join(",",Cells(row).Select(CsvEscape)));
        return Encoding.UTF8.GetBytes("\uFEFF"+sb.ToString());
    }

    public static byte[] Excel(ConfigurationVarianceService.Report report)
    {
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rns="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static string XmlText(string? value)
            =>new string((value??"").Where(c=>c is '\t' or '\n' or '\r' ||
                !char.IsControl(c)).ToArray());
        static string Column(int index)
        {
            var str="";
            for(index++;index>0;index=(index-1)/26)
                str=(char)('A'+((index-1)%26))+str;
            return str;
        }
        XElement MakeRow(int rowNumber,string[] values)
        {
            return new XElement(ns+"row",new XAttribute("r",rowNumber),
                values.Select((value,i)=>new XElement(ns+"c",
                    new XAttribute("r",Column(i)+rowNumber),
                    new XAttribute("t","inlineStr"),
                    new XElement(ns+"is",
                        new XElement(ns+"t",new XAttribute(XNamespace.Xml+"space","preserve"),
                            XmlText(value))))));
        }
        var rows=new List<XElement>{MakeRow(1,Columns)};
        int n=2;
        foreach(var item in report.Rows.Take(5000))
            rows.Add(MakeRow(n++,Cells(item)));
        var sheet=new XDocument(new XDeclaration("1.0","UTF-8","yes"),
            new XElement(ns+"worksheet",new XElement(ns+"sheetData",rows)));
        var workbook=new XDocument(new XDeclaration("1.0","UTF-8","yes"),
            new XElement(ns+"workbook",new XAttribute(XNamespace.Xmlns+"r",rns),
                new XElement(ns+"sheets",
                    new XElement(ns+"sheet",new XAttribute("name","JCF Variance"),
                        new XAttribute("sheetId",1),new XAttribute(rns+"id","rId1")))));
        var types=new XDocument(new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types")+"Types",
            new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types")+"Default",
                new XAttribute("Extension","rels"),
                new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types")+"Default",
                new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),
            new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types")+"Override",
                new XAttribute("PartName","/xl/workbook.xml"),
                new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types")+"Override",
                new XAttribute("PartName","/xl/worksheets/sheet1.xml"),
                new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
        var rels=new XDocument(new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships")+"Relationships",
            new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships")+"Relationship",
                new XAttribute("Id","rId1"),
                new XAttribute("Type","http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target","xl/workbook.xml"))));
        var wrels=new XDocument(new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships")+"Relationships",
            new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships")+"Relationship",
                new XAttribute("Id","rId1"),
                new XAttribute("Type","http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target","worksheets/sheet1.xml"))));
        using var stream=new MemoryStream();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        {
            void Put(string name,XDocument content)
            {
                var entry=zip.CreateEntry(name,CompressionLevel.Optimal);
                using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));
                content.Save(writer);
            }
            Put("[Content_Types].xml",types);
            Put("_rels/.rels",rels);
            Put("xl/workbook.xml",workbook);
            Put("xl/_rels/workbook.xml.rels",wrels);
            Put("xl/worksheets/sheet1.xml",sheet);
        }
        return stream.ToArray();
    }

    public static byte[] Pdf(ConfigurationVarianceService.Report report)
    {
        // Portable PDF 1.4, no embedded fonts, no external libraries. Plain-text
        // audit printout with explicit per-line labels and paginated records.
        static string Safe(string value)
        {
            var text=new string((value??"").Select(c=>c is >= ' ' and <= '~'?c:'?').ToArray());
            text=text.Replace("\\","\\\\").Replace("(","\\(").Replace(")","\\)");
            return text.Length<=117?text:text[..117];
        }
        var lines=new List<string>{
            "JCF INTELLIGENT NETWORK MANAGER - CONFIGURATION VARIANCE",
            "Generated UTC: "+report.GeneratedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            "Equipment: "+report.Devices+" | Verified drift: "+report.DriftFindings+
            " | Pending: "+report.PendingChanges+" | Unknown measurements: "+report.UnknownMeasurements,
            "PROVENANCE: G Global, S Site, D Device, O Optimization, I Imported, ! Verified drift, P Pending",
            "Observed Unknown / Uncertified does NOT mean compliant.",
            "-----------------------------------------------------------------------"
        };
        foreach(var row in report.Rows.Take(5000))
        {
            lines.Add(Safe(row.Device+" | "+row.Setting+" | "+row.Indicator+" | "+row.Quality));
            lines.Add(Safe("  desired="+row.Effective+" | actual="+row.Actual));
        }
        var pages=lines.Chunk(46).Select(x=>x.ToArray()).ToArray();
        if(pages.Length==0)pages=new[]{Array.Empty<string>()};
        var fontId=3+pages.Length*2;
        var objects=new Dictionary<int,byte[]>();
        objects[1]=Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[2]=Encoding.ASCII.GetBytes("<< /Type /Pages /Count "+pages.Length+" /Kids ["+
            string.Join(" ",Enumerable.Range(0,pages.Length).Select(i=>(3+i*2)+" 0 R"))+"] >>");
        for(int i=0;i<pages.Length;i++)
        {
            var pageId=3+i*2;var contentId=pageId+1;
            var instructions=new StringBuilder("BT /F1 9 Tf 11 TL 38 756 Td\n");
            foreach(var line in pages[i])
                instructions.Append('(').Append(Safe(line)).Append(") Tj T*\n");
            instructions.Append("ET\n");
            var bytes=Encoding.ASCII.GetBytes(instructions.ToString());
            objects[pageId]=Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R "+
                "/MediaBox [0 0 612 792] /Resources << /Font << /F1 "+fontId+
                " 0 R >> >> /Contents "+contentId+" 0 R >>");
            using var st=new MemoryStream();
            void Write(string value){var b=Encoding.ASCII.GetBytes(value);st.Write(b);}
            Write("<< /Length "+bytes.Length+" >>\nstream\n");
            st.Write(bytes);Write("endstream");
            objects[contentId]=st.ToArray();
        }
        objects[fontId]=Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var file=new MemoryStream();
        void Append(string content){var b=Encoding.ASCII.GetBytes(content);file.Write(b);}
        Append("%PDF-1.4\n");
        var offsets=new long[fontId+1];
        for(int id=1;id<=fontId;id++){
            offsets[id]=file.Position;
            Append(id+" 0 obj\n");file.Write(objects[id]);Append("\nendobj\n");
        }
        var xref=file.Position;
        Append("xref\n0 "+(fontId+1)+"\n0000000000 65535 f \n");
        for(int id=1;id<=fontId;id++)
            Append(offsets[id].ToString("D10")+" 00000 n \n");
        Append("trailer\n<< /Size "+(fontId+1)+" /Root 1 0 R >>\nstartxref\n"+
            xref+"\n%%EOF\n");
        return file.ToArray();
    }
}