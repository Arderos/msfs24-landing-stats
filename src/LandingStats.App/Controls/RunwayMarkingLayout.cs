using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LandingStats.App.Models;
using LandingStats.Core;

namespace LandingStats.App.Controls;

// WPF outlines are presentation resources; all dimensions and decisions belong
// to the numerical model used by background diagnostics too.
internal sealed class RunwayMarkingLayout
{
    internal sealed class Paint
    {
        public string Kind { get; set; } = "";
        public Point[] Points { get; set; } = System.Array.Empty<Point>();
        public string? Text { get; set; }
        public Geometry? Glyph { get; set; }
        public Rect DesignationRow { get; set; } = Rect.Empty;
        public bool Secondary { get; set; }
    }
    internal static readonly double ReferenceApproachDistance = RunwayMarkingModel.ReferenceApproachDistance;
    public List<Paint> Shapes { get; } = new List<Paint>();
    public bool UsesFaa { get; private set; }
    public static bool UsesFaaConvention(RunwayFacility runway) => RunwayMarkingModel.UsesFaaConvention(runway);
    internal static RunwayMarkingModel.Approach? ResolveApproach(RunwayFacility runway, bool secondary, TelemetryDecisionJournal? journal = null)
        => RunwayMarkingModel.ResolveApproach(runway, secondary, journal);
    public static RunwayMarkingLayout Build(RunwayFacility runway, TelemetryDecisionJournal? journal = null)
    {
        var model = RunwayMarkingModel.Build(runway, journal);
        var result = new RunwayMarkingLayout { UsesFaa = model.UsesFaa };
        foreach (var shape in model.Shapes)
            result.Shapes.Add(new Paint
            {
                Kind = shape.Kind, Text = shape.Text, Secondary = shape.Secondary,
                Points = shape.Points.Select(p => new Point(p.X, p.Y)).ToArray(),
                Glyph = shape.Glyph == null ? null : RunwayDesignationGlyphs.Build(shape.Glyph.Text, model.UsesFaa, shape.Glyph.Narrow),
                DesignationRow = shape.Text == null ? Rect.Empty : new Rect(shape.DesignationRow.X, shape.DesignationRow.Y, shape.DesignationRow.Width, shape.DesignationRow.Height)
            });
        return result;
    }
}
