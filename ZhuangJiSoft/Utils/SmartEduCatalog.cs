namespace DeskApp.Utils;

/// <summary>智慧教育平台筛选项：显示名 + 平台 tag_id。</summary>
public sealed record SmartEduOption(string Name, string Id)
{
    public override string ToString() => Name;
}

/// <summary>一次教材查询的条件。</summary>
public sealed class SmartEduQuery
{
    public string StageId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string EditionId { get; set; } = string.Empty;
    public string GradeId { get; set; } = string.Empty;
    public string Keyword { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 智慧教育平台（basic.smartedu.cn）教材页的维度定义与 tag 树解析。
/// 平台筛选项全部来自 tags/tch_material_tag.json，结构会随平台改版变化，
/// 这里按 tag_dimension_id 递归提取，不写死任何选项列表。
/// </summary>
public static class SmartEduCatalog
{
    public const string BaseUrl = "https://basic.smartedu.cn";
    public const string TagDimensionsUrl =
        "https://s-file-1.ykt.cbern.com.cn/zxx/ndrs/tags/tch_material_tag.json";
    public const string VersionUrl =
        "https://s-file-1.ykt.cbern.com.cn/zxx/ndrs/resources/tch_material/version/data_version.json";
    public const string DetailUrlTemplate =
        "https://s-file-1.ykt.cbern.com.cn/zxx/ndrv2/resources/tch_material/details/{0}.json";

    // 页面上的四级筛选：学段 → 学科 → 版本 → 年级
    public const string DimStage = "zxxxd";
    public const string DimSubject = "zxxxk";
    public const string DimEdition = "zxxbb";
    public const string DimGrade = "zxxnj";
    // 特殊教育的学段下是「盲校/聋校/培智学校」，也当作“学科”一级展示
    public const string DimSpecialType = "zxxlb";

    /// <summary>所有筛选项，按维度分组；Id 为平台 tag_id。</summary>
    public sealed class TagTree
    {
        public List<SmartEduOption> Stages { get; } = [];
        public Dictionary<string, List<SmartEduOption>> SubjectsByStage { get; } = new();
        public Dictionary<string, List<SmartEduOption>> EditionsBySubject { get; } = new();
        public Dictionary<string, List<SmartEduOption>> GradesByEdition { get; } = new();

        public IReadOnlyList<SmartEduOption> Subjects(string stageId) =>
            SubjectsByStage.TryGetValue(stageId, out var v) ? v : [];

        public IReadOnlyList<SmartEduOption> Editions(string subjectId) =>
            EditionsBySubject.TryGetValue(subjectId, out var v) ? v : [];

        public IReadOnlyList<SmartEduOption> Grades(string editionId) =>
            GradesByEdition.TryGetValue(editionId, out var v) ? v : [];
    }

    /// <summary>
    /// 从平台标签 JSON 中按维度提取四级联动选项。
    /// 节点形如 { tag_id, tag_name, tag_dimension_id, hierarchies:[{children:[...]}] }。
    /// </summary>
    public static TagTree ParseTagTree(System.Text.Json.JsonElement root)
    {
        var tree = new TagTree();
        if (!root.TryGetProperty("hierarchies", out var hierarchies))
        {
            return tree;
        }

        // 电子教材根节点下第一层就是学段
        var stages = CollectByDimension(hierarchies, DimStage);
        foreach (var stage in stages)
        {
            tree.Stages.Add(stage);
            var stageNode = FindNode(hierarchies, stage.Id);
            if (stageNode is null)
            {
                continue;
            }

            var subjects = CollectByDimension(stageNode.Value, DimSubject);
            if (subjects.Count == 0)
            {
                subjects = CollectByDimension(stageNode.Value, DimSpecialType);
            }

            tree.SubjectsByStage[stage.Id] = subjects;
            foreach (var subject in subjects)
            {
                var subjectNode = FindNode(stageNode.Value, subject.Id);
                if (subjectNode is null)
                {
                    continue;
                }

                var editions = CollectByDimension(subjectNode.Value, DimEdition);
                tree.EditionsBySubject[subject.Id] = editions;
                foreach (var edition in editions)
                {
                    var editionNode = FindNode(subjectNode.Value, edition.Id);
                    tree.GradesByEdition[edition.Id] = editionNode is null
                        ? []
                        : CollectByDimension(editionNode.Value, DimGrade);
                }
            }
        }

        return tree;
    }

    private static List<SmartEduOption> CollectByDimension(
        System.Text.Json.JsonElement node, string dimension)
    {
        var result = new List<SmartEduOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(node, (id, name, dim) =>
        {
            if (dim == dimension && id.Length > 0 && name.Length > 0 && seen.Add(id))
            {
                result.Add(new SmartEduOption(name, id));
            }
        });
        return result;
    }

    private static System.Text.Json.JsonElement? FindNode(
        System.Text.Json.JsonElement node, string tagId)
    {
        if (node.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var found = FindNode(item, tagId);
                if (found is not null)
                {
                    return found;
                }
            }
            return null;
        }

        if (node.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (node.TryGetProperty("tag_id", out var idEl) &&
            string.Equals(idEl.GetString(), tagId, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var key in new[] { "hierarchies", "children" })
        {
            if (node.TryGetProperty(key, out var child))
            {
                var found = FindNode(child, tagId);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static void Walk(System.Text.Json.JsonElement node,
        Action<string, string, string> visit)
    {
        if (node.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
            {
                Walk(child, visit);
            }
            return;
        }

        if (node.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("tag_id", out var idEl))
        {
            var id = idEl.GetString() ?? string.Empty;
            var name = node.TryGetProperty("tag_name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
            var dim = node.TryGetProperty("tag_dimension_id", out var dEl) ? dEl.GetString() ?? string.Empty : string.Empty;
            visit(id, name, dim);
        }

        foreach (var key in new[] { "hierarchies", "children" })
        {
            if (node.TryGetProperty(key, out var child))
            {
                Walk(child, visit);
            }
        }
    }
}
