using System.Windows;
using System.Windows.Controls;
using PhasmoSaveEditor.Schema;
using PhasmoSaveEditor.ViewModels.Fields;

namespace PhasmoSaveEditor.Views;

/// <summary>
/// 按字段的 Kind 选择对应的编辑模板。
/// 模板都是命名资源（x:Key），所以用选择器而非隐式 DataType 匹配。
/// </summary>
public sealed class FieldTemplateSelector : DataTemplateSelector
{
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is not FieldViewModel f || container is not FrameworkElement fe)
            return base.SelectTemplate(item, container);

        string key = f.Kind switch
        {
            FieldKind.Int or FieldKind.EnumInt => "IntFieldTemplate",
            FieldKind.Float => "FloatFieldTemplate",
            FieldKind.Bool => "BoolFieldTemplate",
            FieldKind.String => "StringFieldTemplate",
            FieldKind.Color => "ColorFieldTemplate",
            FieldKind.IntIntDict or FieldKind.StrIntDict => "DictFieldTemplate",
            FieldKind.IntList or FieldKind.StrList => "ListFieldTemplate",
            FieldKind.Outfit => "OutfitFieldTemplate",
            FieldKind.Wardrobe => "WardrobeFieldTemplate",
            FieldKind.Difficulty => "DifficultyFieldTemplate",
            _ => "RawBlockFieldTemplate",
        };
        return fe.TryFindResource(key) as DataTemplate
               ?? fe.TryFindResource("RawBlockFieldTemplate") as DataTemplate;
    }
}
