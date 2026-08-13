using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>Mirrors osTicket faq_category.</summary>
public class KbCategory : TimestampedEntity
{
    public required string Name { get; set; }

    public int? ParentId { get; set; }
    public KbCategory? Parent { get; set; }

    /// <summary>Public categories are visible on the portal KB (osTicket ispublic).</summary>
    public bool IsPublic { get; set; }

    public string Description { get; set; } = "";

    public string? Notes { get; set; }

    public List<FaqArticle> Articles { get; set; } = [];
}

/// <summary>
/// Mirrors osTicket <c>faq</c>, plus helpful-vote counters for the portal's
/// yes/no widget (mockup portal/kb-article.html; no osTicket counterpart).
/// </summary>
public class FaqArticle : TimestampedEntity
{
    public int CategoryId { get; set; }
    public KbCategory? Category { get; set; }

    public bool IsPublished { get; set; }

    /// <summary>Title (osTicket question).</summary>
    public required string Question { get; set; }

    /// <summary>HTML body (osTicket answer).</summary>
    public required string Answer { get; set; }

    /// <summary>Search keywords (osTicket keywords).</summary>
    public string? Keywords { get; set; }

    public string? Notes { get; set; }

    public int HelpfulYes { get; set; }
    public int HelpfulNo { get; set; }

    public List<FaqArticleTopic> HelpTopics { get; set; } = [];
}

/// <summary>Help topics an article is related to (osTicket faq_topic).</summary>
public class FaqArticleTopic
{
    public int FaqArticleId { get; set; }
    public FaqArticle? FaqArticle { get; set; }

    public int HelpTopicId { get; set; }
    public HelpTopic? HelpTopic { get; set; }
}

/// <summary>
/// Mirrors osTicket <c>canned_response</c>; body supports template variables expanded
/// at insert time by the composer.
/// </summary>
public class CannedResponse : TimestampedEntity
{
    /// <summary>Limit to one department; null = all departments (osTicket dept_id 0).</summary>
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public bool IsEnabled { get; set; } = true;

    public required string Title { get; set; }

    public required string Response { get; set; }

    public string? Notes { get; set; }
}
