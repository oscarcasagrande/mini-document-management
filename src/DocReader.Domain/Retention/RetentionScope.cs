namespace DocReader.Domain.Retention;

/// <summary>What a retention policy applies to, from the most general to the most specific.</summary>
public enum RetentionScope
{
    Global = 0,
    DocumentType = 1,
    ProductService = 2,
    DocumentTypeAndProductService = 3
}
