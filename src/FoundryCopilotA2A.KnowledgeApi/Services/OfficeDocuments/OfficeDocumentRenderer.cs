using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Files;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Services.OfficeDocuments;

/// <summary>
/// Renders validated outlines into Office Open XML packages. Styles and layout are fixed in code,
/// so model output can only supply text.
/// </summary>
public static class OfficeDocumentRenderer
{
    public const string WordMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string PresentationMediaType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const int MaxFileBytes = 4 * 1024 * 1024;

    private const string Creator = "Knowledge Chat";
    private const string Ink = "1F2A44";
    private const string Accent = "0F6CBD";
    private const string Muted = "595959";

    public static GeneratedDocument RenderWord(WordOutline outline) =>
        Bounded(new GeneratedDocument(outline.FileName + ".docx", WordMediaType, WordDocument(outline)));

    public static GeneratedDocument RenderPresentation(PresentationOutline outline) =>
        Bounded(new GeneratedDocument(outline.FileName + ".pptx", PresentationMediaType, Presentation(outline)));

    private static GeneratedDocument Bounded(GeneratedDocument document) =>
        document.Content.Length <= MaxFileBytes
            ? document
            : throw new ApiException(400, "document_too_large", "The rendered document exceeds 4 MB. Shorten it.");

    /// <summary>Splits <c>**bold**</c> markers into runs; unbalanced markers stay literal.</summary>
    internal static IEnumerable<(string Text, bool Bold)> Segments(string text)
    {
        var parts = text.Split("**");
        if (parts.Length % 2 == 0)
        {
            return [(text, false)];
        }
        return parts.Select((part, index) => (part, index % 2 == 1)).Where(segment => segment.part.Length > 0);
    }

    private static byte[] WordDocument(WordOutline outline)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.AddNewPart<StyleDefinitionsPart>().Styles = WordStyles();
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(
                new AbstractNum(
                    new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                    new Level(
                        new StartNumberingValue { Val = 1 },
                        new NumberingFormat { Val = NumberFormatValues.Bullet },
                        new LevelText { Val = "\u2022" },
                        new LevelJustification { Val = LevelJustificationValues.Left },
                        new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
                    { LevelIndex = 0 })
                { AbstractNumberId = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
            main.AddNewPart<DocumentSettingsPart>().Settings = new Settings(new Compatibility(new CompatibilitySetting
            {
                Name = CompatSettingNameValues.CompatibilityMode,
                Uri = "http://schemas.microsoft.com/office/word",
                Val = "15"
            }));

            var body = new Body();
            body.Append(StyledParagraph("Title", outline.Title));
            if (outline.Subtitle.Length > 0)
            {
                body.Append(StyledParagraph("Subtitle", outline.Subtitle));
            }
            foreach (var section in outline.Sections)
            {
                body.Append(StyledParagraph("Heading1", section.Heading));
                foreach (var line in section.Paragraphs.SelectMany(paragraph => paragraph.Split('\n')))
                {
                    body.Append(TextParagraph(line, null));
                }
                foreach (var bullet in section.Bullets)
                {
                    body.Append(TextParagraph(bullet, new ParagraphProperties(
                        new ParagraphStyleId { Val = "ListParagraph" },
                        new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }))));
                }
                if (section.Table is { } table)
                {
                    body.Append(WordTable(table));
                    body.Append(new Paragraph());
                }
            }
            body.Append(new SectionProperties(
                new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 1440, Right = 1440U, Bottom = 1440, Left = 1440U, Header = 708U, Footer = 708U, Gutter = 0U }));
            main.Document = new Document(body);

            document.PackageProperties.Title = outline.Title;
            document.PackageProperties.Creator = Creator;
        }
        return stream.ToArray();
    }

    private static Styles WordStyles()
    {
        static Style ParagraphStyle(string id, string name, StyleParagraphProperties? paragraph, StyleRunProperties? run, bool isDefault = false)
        {
            var style = new Style { Type = StyleValues.Paragraph, StyleId = id, Default = isDefault ? true : null };
            style.Append(new StyleName { Val = name });
            if (!isDefault)
            {
                style.Append(new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" });
            }
            style.Append(new PrimaryStyle());
            if (paragraph is not null)
            {
                style.Append(paragraph);
            }
            if (run is not null)
            {
                style.Append(run);
            }
            return style;
        }

        static RunFonts Fonts(string font) => new() { Ascii = font, HighAnsi = font, ComplexScript = font, EastAsia = font };

        return new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    Fonts("Calibri"), new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "160", Line = "276", LineRule = LineSpacingRuleValues.Auto }))),
            ParagraphStyle("Normal", "Normal", null, null, isDefault: true),
            ParagraphStyle("Title", "Title",
                new StyleParagraphProperties(new SpacingBetweenLines { After = "120" }),
                new StyleRunProperties(Fonts("Calibri Light"), new Color { Val = Ink }, new FontSize { Val = "56" }, new FontSizeComplexScript { Val = "56" })),
            ParagraphStyle("Subtitle", "Subtitle",
                new StyleParagraphProperties(new SpacingBetweenLines { After = "360" }),
                new StyleRunProperties(new Color { Val = Muted }, new FontSize { Val = "28" }, new FontSizeComplexScript { Val = "28" })),
            ParagraphStyle("Heading1", "heading 1",
                new StyleParagraphProperties(new KeepNext(), new KeepLines(), new SpacingBetweenLines { Before = "360", After = "120" }, new OutlineLevel { Val = 0 }),
                new StyleRunProperties(Fonts("Calibri Light"), new Bold(), new Color { Val = Accent }, new FontSize { Val = "32" }, new FontSizeComplexScript { Val = "32" })),
            ParagraphStyle("ListParagraph", "List Paragraph",
                new StyleParagraphProperties(new SpacingBetweenLines { After = "60" }, new Indentation { Left = "720" }, new ContextualSpacing()),
                null));
    }

    private static Paragraph StyledParagraph(string style, string text) =>
        TextParagraph(text, new ParagraphProperties(new ParagraphStyleId { Val = style }));

    private static Paragraph TextParagraph(string text, ParagraphProperties? properties)
    {
        var paragraph = new Paragraph();
        if (properties is not null)
        {
            paragraph.Append(properties);
        }
        foreach (var (segment, bold) in Segments(text))
        {
            var run = new Run();
            if (bold)
            {
                run.Append(new RunProperties(new Bold()));
            }
            run.Append(new Text(segment) { Space = SpaceProcessingModeValues.Preserve });
            paragraph.Append(run);
        }
        return paragraph;
    }

    private static Table WordTable(DocumentTable table)
    {
        static BorderType Border<T>() where T : BorderType, new() =>
            new T { Val = BorderValues.Single, Size = 4U, Space = 0U, Color = "BFBFBF" };

        const int textWidth = 9026;
        var columnWidth = (textWidth / table.Columns.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = new Table(
            new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(Border<TopBorder>(), Border<LeftBorder>(), Border<BottomBorder>(), Border<RightBorder>(),
                    Border<InsideHorizontalBorder>(), Border<InsideVerticalBorder>()),
                new TableLayout { Type = TableLayoutValues.Fixed }),
            new TableGrid(table.Columns.Select(_ => new GridColumn { Width = columnWidth })));

        TableRow Row(IEnumerable<string> cells, bool header)
        {
            var row = new TableRow();
            if (header)
            {
                row.Append(new TableRowProperties(new TableHeader()));
            }
            foreach (var cell in cells)
            {
                var properties = new TableCellProperties(new TableCellWidth { Width = columnWidth, Type = TableWidthUnitValues.Dxa });
                if (header)
                {
                    properties.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "E8EBF0" });
                }
                var paragraph = TextParagraph(header ? $"**{cell.Replace("**", "")}**" : cell,
                    new ParagraphProperties(new SpacingBetweenLines { Before = "40", After = "40" }));
                row.Append(new TableCell(properties, paragraph));
            }
            return row;
        }

        result.Append(Row(table.Columns, header: true));
        foreach (var row in table.Rows)
        {
            result.Append(Row(row, header: false));
        }
        return result;
    }

    // PowerPoint packages are written as XML so the slide structure stays small and reviewable.
    private const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
    private const string GroupShape =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
    private const string ColorMap =
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
        "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;

    private static byte[] Presentation(PresentationOutline outline)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            var master = presentation.AddNewPart<SlideMasterPart>();
            var theme = master.AddNewPart<ThemePart>();
            presentation.AddPart(theme);
            var titleLayout = master.AddNewPart<SlideLayoutPart>();
            var contentLayout = master.AddNewPart<SlideLayoutPart>();
            titleLayout.AddPart(master);
            contentLayout.AddPart(master);
            var notesMaster = presentation.AddNewPart<NotesMasterPart>();
            var notesTheme = notesMaster.AddNewPart<ThemePart>();

            Feed(theme, ThemeXml);
            Feed(notesTheme, ThemeXml);
            Feed(titleLayout, LayoutXml("title", "Title Slide",
                Placeholder(2, "Title 1", "ctrTitle", null, null) + Placeholder(3, "Subtitle 2", "subTitle", 1, null)));
            Feed(contentLayout, LayoutXml("titleOnly", "Title Only", Placeholder(2, "Title 1", "title", null, null)));
            Feed(master, MasterXml(master.GetIdOfPart(titleLayout), master.GetIdOfPart(contentLayout)));
            Feed(notesMaster, NotesMasterXml);

            var slideIds = new List<string>();
            void AddSlide(string xml, SlideLayoutPart layout, string notes)
            {
                var slide = presentation.AddNewPart<SlidePart>();
                slide.AddPart(layout);
                Feed(slide, xml);
                if (notes.Length > 0)
                {
                    var notesSlide = slide.AddNewPart<NotesSlidePart>();
                    notesSlide.AddPart(notesMaster);
                    notesSlide.AddPart(slide);
                    Feed(notesSlide, NotesXml(notes));
                }
                slideIds.Add(presentation.GetIdOfPart(slide));
            }

            AddSlide(TitleSlideXml(outline.Title, outline.Subtitle), titleLayout, "");
            foreach (var slide in outline.Slides)
            {
                AddSlide(ContentSlideXml(slide), contentLayout, slide.Notes);
            }
            Feed(presentation, PresentationXml(presentation.GetIdOfPart(master), presentation.GetIdOfPart(notesMaster), slideIds));

            document.PackageProperties.Title = outline.Title;
            document.PackageProperties.Creator = Creator;
        }
        return stream.ToArray();
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(XmlDeclaration + xml));
        part.FeedData(data);
    }

    private static string Escape(string text) => SecurityElement.Escape(text);

    private static string Transform(long x, long y, long width, long height) =>
        $"<a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm>";

    private static string Placeholder(int id, string name, string type, int? index, string? transform, string? text = null)
    {
        var indexAttribute = index is { } value ? $" idx=\"{value}\"" : "";
        var body = text ?? "<a:p><a:endParaRPr/></a:p>";
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            $"<p:nvPr><p:ph type=\"{type}\"{indexAttribute}/></p:nvPr></p:nvSpPr><p:spPr>{transform}</p:spPr>" +
            $"<p:txBody><a:bodyPr/><a:lstStyle/>{body}</p:txBody></p:sp>";
    }

    private static string Runs(string text, int size, string color, bool bold = false)
    {
        var builder = new StringBuilder();
        foreach (var (segment, segmentBold) in Segments(text))
        {
            builder.Append($"<a:r><a:rPr sz=\"{size}\" b=\"{((bold || segmentBold) ? 1 : 0)}\" dirty=\"0\">" +
                $"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill></a:rPr><a:t>{Escape(segment)}</a:t></a:r>");
        }
        return builder.ToString();
    }

    private static string Rectangle(int id, string name, long x, long y, long width, long height, string color) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr>{Transform(x, y, width, height)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
        $"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>";

    private static string TitleSlideXml(string title, string subtitle)
    {
        var shapes =
            Rectangle(4, "Accent", 838200, 3429000, 1371600, 54864, "4CA3E8") +
            Placeholder(2, "Title 1", "ctrTitle", null, Transform(838200, 1371600, 10515600, 1965960),
                $"<a:p><a:pPr algn=\"l\"/>{Runs(title, title.Length > 70 ? 3600 : 4400, "FFFFFF", bold: true)}</a:p>") +
            Placeholder(3, "Subtitle 2", "subTitle", 1, Transform(838200, 3657600, 10515600, 1143000),
                subtitle.Length > 0
                    ? $"<a:p><a:pPr marL=\"0\" indent=\"0\" algn=\"l\"><a:buNone/></a:pPr>{Runs(subtitle, 2000, "CFD8E6")}</a:p>"
                    : "<a:p><a:pPr marL=\"0\" indent=\"0\"><a:buNone/></a:pPr><a:endParaRPr/></a:p>");
        return $"<p:sld {Namespaces}><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"{Ink}\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>" +
            $"<p:spTree>{GroupShape}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private static string ContentSlideXml(PresentationSlide slide)
    {
        var characters = slide.Bullets.Sum(bullet => bullet.Length);
        var size = characters > 600 || slide.Bullets.Length > 6 ? 1800 : characters > 350 || slide.Bullets.Length > 5 ? 2000 : 2400;
        var bullets = new StringBuilder();
        foreach (var bullet in slide.Bullets)
        {
            bullets.Append("<a:p><a:pPr marL=\"342900\" indent=\"-342900\"><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef>" +
                $"<a:buClr><a:srgbClr val=\"{Accent}\"/></a:buClr><a:buFont typeface=\"Arial\"/><a:buChar char=\"\u2022\"/></a:pPr>" +
                $"{Runs(bullet, size, "262626")}</a:p>");
        }
        var shapes =
            Placeholder(2, "Title 1", "title", null, Transform(838200, 365125, 10515600, 1188720),
                $"<a:p>{Runs(slide.Title, slide.Title.Length > 60 ? 2800 : 3600, Ink, bold: true)}</a:p>") +
            Rectangle(3, "Accent", 838200, 1600200, 1371600, 45720, Accent) +
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"4\" name=\"Content 3\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
            $"<p:spPr>{Transform(838200, 1828800, 10515600, 4572000)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>" +
            $"<p:txBody><a:bodyPr wrap=\"square\" anchor=\"t\"><a:normAutofit/></a:bodyPr><a:lstStyle/>{bullets}</p:txBody></p:sp>";
        return $"<p:sld {Namespaces}><p:cSld><p:spTree>{GroupShape}{shapes}</p:spTree></p:cSld>" +
            "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private static string NotesXml(string notes)
    {
        var paragraphs = string.Concat(notes.Split('\n').Select(line =>
            $"<a:p><a:r><a:rPr dirty=\"0\"/><a:t>{Escape(line)}</a:t></a:r></a:p>"));
        return $"<p:notes {Namespaces}><p:cSld><p:spTree>{GroupShape}" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            $"<p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody></p:sp>" +
            "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>";
    }

    private static string PresentationXml(string masterId, string notesMasterId, IEnumerable<string> slideIds) =>
        $"<p:presentation {Namespaces} saveSubsetFonts=\"1\">" +
        $"<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"{masterId}\"/></p:sldMasterIdLst>" +
        $"<p:notesMasterIdLst><p:notesMasterId r:id=\"{notesMasterId}\"/></p:notesMasterIdLst>" +
        $"<p:sldIdLst>{string.Concat(slideIds.Select((id, index) => $"<p:sldId id=\"{256 + index}\" r:id=\"{id}\"/>"))}</p:sldIdLst>" +
        $"<p:sldSz cx=\"{SlideWidth}\" cy=\"{SlideHeight}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>";

    private static string LayoutXml(string type, string name, string placeholders) =>
        $"<p:sldLayout {Namespaces} type=\"{type}\" preserve=\"1\"><p:cSld name=\"{name}\"><p:spTree>{GroupShape}{placeholders}</p:spTree></p:cSld>" +
        "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string MasterXml(string titleLayoutId, string contentLayoutId) =>
        $"<p:sldMaster {Namespaces}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{GroupShape}" +
        Placeholder(2, "Title Placeholder 1", "title", null, Transform(838200, 365125, 10515600, 1325563)) +
        Placeholder(3, "Text Placeholder 2", "body", 1, Transform(838200, 1825625, 10515600, 4351338)) +
        $"</p:spTree></p:cSld>{ColorMap}" +
        $"<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"{titleLayoutId}\"/><p:sldLayoutId id=\"2147483650\" r:id=\"{contentLayoutId}\"/></p:sldLayoutIdLst>" +
        "<p:txStyles>" +
        "<p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"3600\" b=\"1\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill>" +
        "<a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
        "<p:bodyStyle><a:lvl1pPr marL=\"342900\" indent=\"-342900\"><a:buFont typeface=\"Arial\"/><a:buChar char=\"\u2022\"/>" +
        "<a:defRPr sz=\"2400\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/>" +
        "<a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:bodyStyle>" +
        "<p:otherStyle><a:defPPr><a:defRPr/></a:defPPr></p:otherStyle></p:txStyles></p:sldMaster>";

    private static readonly string NotesMasterXml =
        $"<p:notesMaster {Namespaces}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{GroupShape}" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
        $"<p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr><p:spPr>{Transform(685800, 1143000, 5486400, 3086100)}" +
        "<a:noFill/><a:ln w=\"12700\"><a:solidFill><a:prstClr val=\"black\"/></a:solidFill></a:ln></p:spPr></p:sp>" +
        Placeholder(3, "Notes Placeholder 2", "body", 1, Transform(685800, 4400550, 5486400, 3600450)) +
        $"</p:spTree></p:cSld>{ColorMap}</p:notesMaster>";

    private static readonly string ThemeXml =
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Knowledge Chat\"><a:themeElements>" +
        "<a:clrScheme name=\"Knowledge Chat\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        $"<a:dk2><a:srgbClr val=\"{Ink}\"/></a:dk2><a:lt2><a:srgbClr val=\"E8EBF0\"/></a:lt2>" +
        $"<a:accent1><a:srgbClr val=\"{Accent}\"/></a:accent1><a:accent2><a:srgbClr val=\"4CA3E8\"/></a:accent2>" +
        "<a:accent3><a:srgbClr val=\"2E8B57\"/></a:accent3><a:accent4><a:srgbClr val=\"C27C0E\"/></a:accent4>" +
        "<a:accent5><a:srgbClr val=\"8764B8\"/></a:accent5><a:accent6><a:srgbClr val=\"C4314B\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Knowledge Chat\">" +
        "<a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Knowledge Chat\"><a:fillStyleLst>" + Repeat("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>") + "</a:fillStyleLst>" +
        "<a:lnStyleLst>" + string.Concat(new[] { 6350, 12700, 19050 }.Select(width =>
            $"<a:ln w=\"{width}\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>")) + "</a:lnStyleLst>" +
        "<a:effectStyleLst>" + Repeat("<a:effectStyle><a:effectLst/></a:effectStyle>") + "</a:effectStyleLst>" +
        "<a:bgFillStyleLst>" + Repeat("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>") + "</a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements></a:theme>";

    private static string Repeat(string xml) => xml + xml + xml;
}
