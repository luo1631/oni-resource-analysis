using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal sealed partial class ResourcePlanningPanel
{
    private const string TrackedPreference = "OniResourceBalance.TrackedResources";
    private readonly HashSet<string> trackedResources = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingResources = new(StringComparer.Ordinal);
    private ResourceKey[] visibleChoices = Array.Empty<ResourceKey>();
    private View beforePicker;
    private bool onlyChecked;
    private RectTransform pickerView;
    private RectTransform pickerList;
    private KInputTextField pickerSearch;
    private KButton pickerButton;
    private TextMeshProUGUI pickerButtonLabel;
    private TextMeshProUGUI pickerStatus;
    private TextMeshProUGUI onlyCheckedLabel;
    private KButton checkResultsButton;
    private KButton clearChoicesButton;

    private void LoadTrackedResources()
    {
        trackedResources.Clear();
        if (KPlayerPrefs.HasKey(TrackedPreference))
            trackedResources.UnionWith(KPlayerPrefs.GetString(TrackedPreference, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        else
            trackedResources.UnionWith(ResourceKey.Defaults.Select(TrackId));
    }

    private static string TrackId(ResourceKey key) => (int)key.Unit + ":" + key.Id;

    private bool IsTracked(ResourceKey key) => trackedResources.Contains(TrackId(key));

    private void BuildPicker(RectTransform parent)
    {
        pickerView = Rect("ResourcePicker", parent, Vector2.zero, Vector2.one);
        pickerView.gameObject.SetActive(false);
        var search = TopBand("Search", pickerView, ColumnHeight, right: 0.74f);
        var background = search.gameObject.AddComponent<Image>();
        background.color = RowColor;
        pickerSearch = search.gameObject.AddComponent<KInputTextField>();
        pickerSearch.targetGraphic = background;
        var viewport = Rect("TextArea", search, Vector2.zero, Vector2.one);
        viewport.offsetMin = new Vector2(8, 0);
        viewport.offsetMax = new Vector2(-8, 0);
        viewport.gameObject.AddComponent<RectMask2D>();
        pickerSearch.textViewport = viewport;
        pickerSearch.textComponent = Label(viewport, "", BodyTextSize);
        pickerSearch.placeholder = Label(viewport, Tr("搜索资源", "Search resources"), BodyTextSize, color: SecondaryColor);
        pickerSearch.characterLimit = 96;
        pickerSearch.onValueChanged.AddListener(_ => RenderPicker());
        var filter = TopBand("ChoiceFilter", pickerView, ColumnHeight, left: 0.75f);
        var only = MakeButton(filter, "", () =>
        {
            onlyChecked = !onlyChecked;
            RenderPicker();
        }, 0);
        onlyCheckedLabel = only.GetComponentInChildren<TextMeshProUGUI>(true);
        var footer = Rect("ChoiceActions", pickerView, Vector2.zero, Vector2.right);
        footer.offsetMax = new Vector2(0, ToolbarHeight);
        ConfigureControls(footer);
        pickerStatus = FitText(Label(footer, "", BodyTextSize - 1, color: SecondaryColor), MinimumTextSize);
        FlexibleLabel(pickerStatus.rectTransform);
        checkResultsButton = MakeButton(footer, Tr("勾选结果", "Check hits"), () =>
        {
            pendingResources.UnionWith(visibleChoices.Select(TrackId));
            RenderPicker(preservePosition: true);
        }, 115);
        clearChoicesButton = MakeButton(footer, Tr("清空本星", "Clear world"), () =>
        {
            pendingResources.ExceptWith(resources.Select(TrackId));
            RenderPicker(preservePosition: true);
        }, 116);
        MakeButton(footer, Tr("取消", "Cancel"), () => FinishPicker(apply: false), 62);
        pickerList = MakeScrollArea(pickerView, "Choices", Vector2.zero, Vector2.one, ColumnHeight + 3);
        ((RectTransform)pickerList.parent.parent).offsetMin = new Vector2(0, ToolbarHeight + 2);
    }

    private void TogglePicker()
    {
        if (view == View.Picker)
        {
            FinishPicker(apply: true);
            return;
        }

        beforePicker = view;
        pendingResources.Clear();
        pendingResources.UnionWith(trackedResources);
        onlyChecked = false;
        pickerSearch.SetTextWithoutNotify("");
        SetView(View.Picker);
        RenderPicker();
    }

    private void FinishPicker(bool apply, View? destination = null)
    {
        if (apply && !trackedResources.SetEquals(pendingResources))
        {
            string saved = string.Join("\n", pendingResources.OrderBy(value => value, StringComparer.Ordinal));
            KPlayerPrefs.SetString(TrackedPreference, saved);
            trackedResources.Clear();
            trackedResources.UnionWith(pendingResources);
        }
        SetView(destination ?? beforePicker);
        Render();
    }

    private void NavigateTo(View destination)
    {
        if (view == destination)
            return;
        if (view == View.Picker)
        {
            FinishPicker(apply: true, destination);
            return;
        }
        if (destination == View.Table && view == View.Graph && graphFocus?.Resource is ResourceKey key)
        {
            if (selected != key)
                openedSource = null;
            selected = key;
            if (!MatchesFilter(key, balanceFilter))
                balanceFilter = BalanceFilter.All;
        }
        SetView(destination);
        Render();
    }

    private void SetView(View next)
    {
        view = next;
        if (view != View.Picker && pickerSearch.isFocused)
            pickerSearch.DeactivateInputField();
        pickerView.gameObject.SetActive(view == View.Picker);
        tableView.gameObject.SetActive(view == View.Table);
        graphView.gameObject.SetActive(view == View.Graph);
        ranchView.gameObject.SetActive(view == View.Ranch);
        guideView.gameObject.SetActive(view == View.Guide);
        UpdateViewButtons();
    }

    private void UpdateViewButtons()
    {
        int count = resources.Count(IsTracked);
        pickerButtonLabel.text = view == View.Picker ? Tr("应用", "Apply") :
            Tr("选资源", "Resources") + (count > 0 ? $" ({count})" : "");
        pickerButton.isInteractable = view == View.Picker || resources.Length > 0;
        tableButton.bgImage.color = view == View.Table ? HeaderColor : ButtonColor;
        graphButton.bgImage.color = view == View.Graph ? HeaderColor : ButtonColor;
        ranchButton.bgImage.color = view == View.Ranch ? HeaderColor : ButtonColor;
        guideButton.bgImage.color = view == View.Guide ? HeaderColor : ButtonColor;
    }

    private void RenderPicker(bool preservePosition = false)
    {
        Clear(pickerList, preservePosition);
        string query = pickerSearch.text?.Trim() ?? "";
        visibleChoices = resources.Where(key => (!onlyChecked || pendingResources.Contains(TrackId(key))) &&
            (query.Length == 0 || UnitName(key).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
             key.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
        for (int index = 0; index < visibleChoices.Length; index++)
        {
            ResourceKey key = visibleChoices[index];
            string id = TrackId(key);
            Color striped = index % 2 == 0 ? RowColor : AlternateRowColor;
            bool wasChecked = pendingResources.Contains(id);
            KButton choice = null;
            GameObject mark = null;
            choice = MakeButton(pickerList, null, () =>
            {
                bool tracked = pendingResources.Add(id);
                if (!tracked)
                    pendingResources.Remove(id);
                if (onlyChecked)
                {
                    RenderPicker(preservePosition: true);
                    return;
                }
                mark.SetActive(tracked);
                choice.bgImage.color = tracked ? SelectedColor : striped;
                UpdatePickerStatus();
            }, 0, wasChecked ? SelectedColor : striped);
            var rect = (RectTransform)choice.transform;
            mark = AddCheckMark(rect);
            mark.SetActive(wasChecked);
            var nameCell = Rect("Name", rect, Vector2.zero, Vector2.one);
            nameCell.offsetMin = new Vector2(compactUi ? 15 : 24, 0);
            FitText(Label(WithIcon(nameCell, key.Id, compactUi ? 12 : 18), UnitName(key), BodyTextSize), MinimumTextSize);
        }
        if (visibleChoices.Length == 0)
            AddMessage(pickerList, Tr("没有匹配的资源。", "No matching resources."), SecondaryColor);
        UpdatePickerStatus();
    }

    private void UpdatePickerStatus()
    {
        int checkedCount = resources.Count(key => pendingResources.Contains(TrackId(key)));
        pickerStatus.text = Tr($"已选 {checkedCount} · 显示 {visibleChoices.Length}", $"{checkedCount} checked · {visibleChoices.Length} shown");
        onlyCheckedLabel.text = onlyChecked ? Tr("显示全部", "Show all") : Tr("仅已选", "Checked only");
        checkResultsButton.isInteractable = visibleChoices.Any(key => !pendingResources.Contains(TrackId(key)));
        clearChoicesButton.isInteractable = checkedCount > 0;
    }

    private GameObject AddCheckMark(RectTransform row)
    {
        var box = Rect("Checkbox", row, new Vector2(0, 0.5f), new Vector2(0, 0.5f));
        box.sizeDelta = Vector2.one * (compactUi ? 10 : 16);
        box.anchoredPosition = new Vector2(compactUi ? 9 : 16, 0);
        var outline = box.gameObject.AddComponent<Image>();
        outline.color = SecondaryColor;
        outline.raycastTarget = false;
        var inner = Rect("Interior", box, Vector2.zero, Vector2.one);
        inner.offsetMin = Vector2.one;
        inner.offsetMax = -Vector2.one;
        var white = inner.gameObject.AddComponent<Image>();
        white.color = RowColor;
        white.raycastTarget = false;
        var mark = Rect("Checked", inner, Vector2.zero, Vector2.one);
        var fill = mark.gameObject.AddComponent<Image>();
        fill.color = HeaderColor;
        fill.raycastTarget = false;
        AddTickStroke(mark, new Vector2(-2.6f, -1.4f), 4.3f, -45);
        AddTickStroke(mark, new Vector2(1.3f, 0.1f), 7.2f, 45);
        return mark.gameObject;
    }

    private void AddTickStroke(RectTransform parent, Vector2 position, float width, float angle)
    {
        var center = new Vector2(0.5f, 0.5f);
        var stroke = Rect("Tick", parent, center, center);
        float scale = compactUi ? 0.58f : 1;
        stroke.sizeDelta = new Vector2(width, 1.7f) * scale;
        stroke.anchoredPosition = position * scale;
        stroke.localEulerAngles = new Vector3(0, 0, angle);
        var image = stroke.gameObject.AddComponent<Image>();
        image.color = HeaderTextColor;
        image.raycastTarget = false;
    }
}
