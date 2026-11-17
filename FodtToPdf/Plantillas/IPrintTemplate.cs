using System.Data;

namespace FodtToPdf
{
    /// <summary>
    /// Contract used to fill a print template (the flat .fodt template used by this package).
    /// </summary>
    public interface IPrintTemplate
    {
        /// <summary>Replaces the content of a bookmark when it exists. Returns false when the bookmark is missing.</summary>
        bool SetBookmarkIfExists(string name, string value);

        /// <summary>Fills the loose header fields from a record and returns how many fields were filled.</summary>
        int ExecuteMailMerge(DataRow record);

        /// <summary>Expands a repeating table region and returns the number of rendered rows.</summary>
        int ExecuteRegion(string regionName, DataTable data);

        /// <summary>Inserts an image into a bookmark using an explicit size in points.</summary>
        bool InsertImage(string bookmarkName, byte[] image, double widthPt, double heightPt);

        /// <summary>Inserts an image into a bookmark using its natural size.</summary>
        bool InsertImageNatural(string bookmarkName, byte[] image);

        /// <summary>Reflows fixed-position text boxes so their content does not overlap.</summary>
        int ReflowTextBoxes();

        /// <summary>Returns the filled document as bytes.</summary>
        byte[] ToBytes();
    }
}
