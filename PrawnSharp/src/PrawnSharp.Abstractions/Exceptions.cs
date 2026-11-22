namespace PrawnSharp;

/// <summary>Error base de PrawnSharp.</summary>
public class PrawnException : Exception
{
    public PrawnException()
    {
    }

    public PrawnException(string message) : base(message)
    {
    }

    public PrawnException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>La fuente pedida no se puede resolver en la fase actual.</summary>
public sealed class UnsupportedFontException : PrawnException
{
    public UnsupportedFontException()
    {
    }

    public UnsupportedFontException(string message) : base(message)
    {
    }

    public UnsupportedFontException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>La operación existe en la API pero todavía no está implementada.</summary>
public sealed class UnsupportedFeatureException : PrawnException
{
    public UnsupportedFeatureException()
    {
    }

    public UnsupportedFeatureException(string message) : base(message)
    {
    }

    public UnsupportedFeatureException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
