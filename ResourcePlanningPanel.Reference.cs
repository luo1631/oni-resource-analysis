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
    private RectTransform ranchView;
    private RectTransform ranchAnimals;
    private RectTransform ranchDetails;
    private RectTransform guideView;
    private KButton ranchButton;
    private KButton guideButton;
    private TextMeshProUGUI ranchDetailTitle;
    private TextMeshProUGUI guideColony;
    private int? selectedAnimal;
    private readonly Dictionary<int, (KImage Image, Color Background)> animalRows = new();

    private void BuildRanchView(RectTransform parent)
    {
        ranchView = Rect("RanchView", parent, Vector2.zero, Vector2.one);
        var animalHeading = TopBand("AnimalHeading", ranchView, ColumnHeight, right: 0.46f);
        var residentsLabel = Label(animalHeading, Tr("畜舍动物", "Stable residents"), BodyTextSize);
        residentsLabel.margin = new Vector4(8, 0, 4, 0);
        Place(residentsLabel.rectTransform, Vector2.zero, new Vector2(0.65f, 1));
        var forecastLabel = FitText(Label(animalHeading, Tr("未来 10 周期", "Next 10 cycles"), BodyTextSize - 1,
            TextAlignmentOptions.Right, SecondaryColor), MinimumTextSize);
        Place(forecastLabel.rectTransform, new Vector2(0.65f, 0), new Vector2(0.98f, 1));
        var detailHeading = TopBand("ForecastHeading", ranchView, ColumnHeight, left: 0.48f);
        ranchDetailTitle = FitText(Label(detailHeading, Tr("繁殖和补给", "Breeding and feed"), BodyTextSize), MinimumTextSize);
        ranchDetailTitle.margin = new Vector4(8, 0, 4, 0);
        ranchAnimals = MakeScrollArea(ranchView, "AnimalList", Vector2.zero, new Vector2(0.46f, 1), ColumnHeight);
        ranchDetails = MakeScrollArea(ranchView, "RanchDetails", new Vector2(0.48f, 0), Vector2.one, ColumnHeight);
        ranchView.gameObject.SetActive(false);
    }

    private void RenderRanches()
    {
        Clear(ranchAnimals, preservePosition: true);
        animalRows.Clear();
        var ranches = world?.Ranches;
        if (ranches == null || ranches.Count == 0)
        {
            selectedAnimal = null;
            Clear(ranchDetails);
            ranchDetailTitle.text = Tr("繁殖和补给", "Breeding and feed");
            AddMessage(ranchAnimals, Tr("当前星球的畜舍里没有驯化动物。", "No tamed critters in stables on this world."), SecondaryColor);
            return;
        }

        var visible = ranches.SelectMany(ranch => ranch.Animals.Select(animal => (Ranch: ranch, Animal: animal))).ToArray();
        var selected = visible.FirstOrDefault(row => row.Animal.Id == selectedAnimal);
        if (selected.Animal == null)
            selected = visible.FirstOrDefault(row => !row.Animal.AfterMaturity && row.Animal.LifetimeEggs.HasValue);
        if (selected.Animal == null)
            selected = visible[0];

        foreach (var ranch in ranches.OrderBy(item => item.Name))
        {
            AddMessage(ranchAnimals, ranch.Name + $" · {ranch.Animals.Count}", HeaderColor);
            int index = 0;
            foreach (var animal in ranch.Animals.OrderBy(item => item.PrefabId).ThenBy(item => item.Name).ThenBy(item => item.Id))
            {
                Color background = index++ % 2 == 0 ? RowColor : AlternateRowColor;
                var button = MakeButton(ranchAnimals, null, () => ShowAnimal(ranch, animal), 0, background);
                var row = (RectTransform)button.transform;
                var nameCell = Rect("Animal", row, Vector2.zero, new Vector2(0.70f, 1));
                FitText(Label(WithIcon(nameCell, animal.PrefabId, compactUi ? 12 : 18), animal.Name, BodyTextSize), MinimumTextSize);
                double? near = animal.FedBufferEggs ?? animal.WindowEggs;
                var summary = FitText(Label(row, Eggs(near), BodyTextSize, TextAlignmentOptions.Right, SecondaryColor), MinimumTextSize);
                Place(summary.rectTransform, new Vector2(0.69f, 0), new Vector2(0.98f, 1));
                string basis = animal.FedBufferEggs.HasValue ? Tr("按现有储备估算", "With current reserves") :
                    Tr("持续补料条件下", "With continuous feeding");
                button.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(animal.Name + "\n" + animal.Status +
                    "\n" + Tr("未来 10 周期 · ", "Next 10 cycles · ") + basis + " · " + Eggs(near));
                animalRows[animal.Id] = (button.bgImage, background);
            }
        }
        ShowAnimal(selected.Ranch, selected.Animal);
    }

    private void ShowAnimal(RanchReport ranch, AnimalForecast animal)
    {
        Clear(ranchDetails, preservePosition: selectedAnimal == animal.Id);
        selectedAnimal = animal.Id;
        foreach (var row in animalRows)
            row.Value.Image.color = row.Key == animal.Id ? SelectedColor : row.Value.Background;

        ranchDetailTitle.text = animal.Name;
        AddMessage(ranchDetails, animal.Status, HeaderColor, animal.PrefabId);
        AddRanchMetric(Tr("年龄 / 自然寿命", "Age / Natural lifespan"),
            Display(animal.Age) + " / " + Cycles(animal.NaturalLife));
        if (animal.MaturesIn.HasValue)
            AddRanchMetric(Tr("距成年", "Time to adulthood"), Cycles(animal.MaturesIn));
        AddRanchMetric(Tr("理论终生产蛋", "Estimated lifetime eggs"), Eggs(animal.LifetimeEggs));
        AddRanchMetric(Tr("余生预计产蛋", "Expected remaining eggs"), Eggs(animal.RemainingEggs));
        AddRanchMetric(Tr("下次产蛋 / 产蛋间隔", "Next egg / Interval"),
            Display(animal.NextEgg) + " / " + Cycles(animal.EggInterval));
        AddRanchMetric(Tr("持续补料 · 未来 10 周期", "With refills · next 10 cycles"), Eggs(animal.WindowEggs));
        if (animal.EggsThrough(3) is double first && animal.EggsThrough(6) is double second && animal.WindowEggs is double total)
        {
            AddMessage(ranchDetails, Tr("持续补料 · 分段产蛋预测", "Eggs by period with refills") + "\n" +
                Tr($"第 1–3 周期 {Eggs(first)} · 第 4–6 周期 {Eggs(second - first)} · 第 7–10 周期 {Eggs(total - second)}",
                   $"Cycles 1–3 {Eggs(first)} · 4–6 {Eggs(second - first)} · 7–10 {Eggs(total - second)}"), SecondaryColor);
        }
        if (animal.FedBufferEggs.HasValue)
            AddRanchMetric(Tr("料斗和自身储备 · 未来 10 周期", "Feeder and body reserves · next 10 cycles"), Eggs(animal.FedBufferEggs));
        if (animal.NaturalReplacement.HasValue)
            AddRanchMetric(Tr("新蛋自然孵化至成年", "New egg to adult, natural incubation"), Cycles(animal.NaturalReplacement));

        AddMessage(ranchDetails, Tr("本畜舍料斗", "Stable feeder supply"), HeaderColor);
        foreach (var feed in ranch.Feeds.OrderBy(item => GameText.Label(item.Id)))
        {
            string stock = GameText.Label(feed.Id) + " · " +
                Tr("预计消耗 ", "Demand ") + Number(feed.KgPerCycle) + Tr(" kg/周期", " kg/cycle");
            string remaining = Tr("料斗现有 ", "Feeder holds ") + Number(feed.StockKg) + Tr(" kg · 可撑约 ", " kg · about ") +
                Cycles(feed.StockKg / feed.KgPerCycle);
            AddMessage(ranchDetails, stock + "\n" + remaining, color: SecondaryColor, icon: feed.Id);
        }
        if (ranch.Feeds.Count == 0)
            AddMessage(ranchDetails, Tr("料斗可用量待确认。多选食谱或放牧的用量可在对应资源明细查看。",
                "Feeder supply is unconfirmed. See individual resources for alternative diets and grazing."), SecondaryColor);
    }

    private void AddRanchMetric(string name, string value)
    {
        var row = Rect("Metric", ranchDetails, Vector2.zero, Vector2.one);
        row.gameObject.AddComponent<LayoutElement>().minHeight = ButtonHeight + 1;
        var caption = FitText(Label(row, name, BodyTextSize, color: SecondaryColor), MinimumTextSize);
        Place(caption.rectTransform, Vector2.zero, new Vector2(0.60f, 1));
        caption.margin = new Vector4(8, 0, 3, 0);
        var text = FitText(Label(row, value, BodyTextSize, TextAlignmentOptions.Right), MinimumTextSize);
        Place(text.rectTransform, new Vector2(0.60f, 0), new Vector2(0.98f, 1));
    }

    private static string Display(double? value) => value.HasValue ? Number(value.Value) : "—";
    private static string Cycles(double? value) => value.HasValue ? Number(value.Value) + Tr(" 周期", " cycles") : "—";
    private static string Eggs(double? value) => value.HasValue ? Number(value.Value) + Tr(" 枚", " eggs") : "—";

    private void BuildGuide(RectTransform parent)
    {
        guideView = Rect("GuideView", parent, Vector2.zero, Vector2.one);
        var content = MakeScrollArea(guideView, "GuideTopics", Vector2.zero, Vector2.one);
        ((RectTransform)content.parent.parent).offsetMin = new Vector2(0, ToolbarHeight + 1);
        guideColony = FitText(Label(content, "", BodyTextSize, color: HeaderColor), MinimumTextSize);
        guideColony.margin = new Vector4(8, 4, 8, 4);
        guideColony.gameObject.AddComponent<LayoutElement>().minHeight = ButtonHeight + 5;

        AddGuideSection(content, Tr("读收支表", "Reading the balance"),
            Tr("连续流量 × 600 得到每周期理论量。右侧＋为产出、－为消耗，点来源可看配方、工况、房间和备选条件。备选量单列。\n" +
                "理论在打开面板、换周期或手动刷新时重算。周期标签可查快照时间。实测覆盖已接入的建筑、复制人呼吸和畜舍产蛋。0 为已观测零流量，— 为缺数据，† 为上周期部分覆盖。",
                "A continuous rate × 600 gives the estimate per cycle. On the right, + marks output and − marks input. Open a source for its recipe, operating conditions, rooms and options. Alternatives are shown separately.\n" +
                "Theory refreshes on opening, rollover or Refresh. Hover over the cycle for its snapshot time. Actuals cover metered buildings, duplicant breathing and stable eggs. 0 is observed zero flow, — is missing and † is partial coverage last cycle."));
        AddGuideSection(content, Tr("近 10 周期均值", "10-cycle means"),
            Tr("近 10 个已结束周期内，产出和消耗分别按计量完整的样本求均值。缺测跳过，已观测的 0 计入。\n" +
                "完整周期汇总随存档保存。3 秒明细只保留在当前游戏会话。",
                "Mean output and input use complete metered samples from the last 10 ended cycles. Missing cycles are skipped and observed zeros count.\n" +
                "Complete-cycle totals travel with the save. Three-second detail is kept for the current game session."));
        AddGuideSection(content, Tr("复制人每周期需求", "Duplicant requirements"),
            Tr("1 周期 = 600 游戏秒。默认难度下，无修正的普通复制人每周期需要约 1,000 kcal 和 60 kg 可呼吸气体。\n" +
                "口粮和耗气按个体难度、特质和效果修正。实测按游戏实际吸入的气体分开累计，服装和仿生氧气罐按吸入时结算。换版前未计呼吸的旧周期不参与对应耗气均值。",
                "One cycle is 600 game seconds. A regular duplicant on default difficulty needs about 1,000 kcal and 60 kg of breathable gas per cycle before modifiers.\n" +
                "The balance uses each duplicant's live modifiers. Actual breathing is recorded by gas when consumed, including suit and bionic tanks. Older cycles without breathing are omitted from those gas-use averages."));
        AddGuideSection(content, Tr("库存、缺口和缓冲", "Stock, deficits and buffers"),
            Tr("可撑周期 = 可用库存 ÷ 每周期理论净缺口。库存取本星球可调拨物资。地格和管网的存量可结合元素覆盖和管道状态另行核对。\n" +
                "停供期间的缓冲量 ≈ 净消耗速率 × 停供秒数。例如停供 120 秒、缺口 2 kg/s，需要约 240 kg 的可用缓冲。",
                "Cycles of cover = available stock ÷ estimated net deficit per cycle. Stock uses the world's available supplies. Check map tiles and conduits for material held in those systems.\n" +
                "Buffer needed during a supply gap ≈ net demand rate × gap duration. A deficit of 2 kg/s over 120 seconds needs about 240 kg of usable buffer."));
        AddGuideSection(content, Tr("电解与用水", "Electrolysis and water"),
            Tr("满载电解器每秒用 1 kg 水，产生 0.888 kg 氧气和 0.112 kg 氢气。目标氧量 ÷ 0.888 = 理想用水量。10 名无修正复制人每周期需氧 600 kg，对应约 676 kg 水，还须给停机和管路留余量。",
                "A full-speed electrolyzer uses 1 kg water per second to make 0.888 kg oxygen and 0.112 kg hydrogen. Divide the oxygen target by 0.888 for ideal water demand. Ten unmodified duplicants need 600 kg oxygen per cycle, or about 676 kg water, with extra capacity for downtime and pipes."));
        AddGuideSection(content, Tr("收获与食物库存", "Harvest and food stock"),
            Tr("热量产率 = 每次收获的可食热量 ÷ 收获间隔。假设每次 600 kcal，生长 3 周期且及时采收，每周期约 200 kcal。若关掉自动采收，成熟后再等 4 周期自然掉果，约为 86 kcal/周期。\n" +
                "总热量净额可抵消加工已计入的原料热量。冷藏延缓腐败，常见易腐食物需要深冷（约 −18°C 或更低）与无菌气氛共同停止基准腐败。库存是当下读数。",
                "Calorie yield = edible calories per harvest ÷ time between harvests. At 600 kcal per harvest, three cycles of growth and prompt harvesting yield 200 kcal/cycle. Waiting four extra cycles for a natural drop gives about 86 kcal/cycle.\n" +
                "Net food energy offsets counted calories used as recipe ingredients. Refrigeration slows spoilage. Common perishables need a deep freeze near −18°C or colder and a sterile atmosphere to stop base spoilage. Stock is a snapshot."));
        AddGuideSection(content, Tr("加工排单与工时", "Recipe queues and work time"),
            Tr("预计工时 = 配方时间 × 原生时间倍率 ÷ 工速。照明、实验室和支援建筑只对适用的工作生效。在制订单按剩余工时计。每周期最多分配 600 游戏秒，实际产量还受送货、缺料和工作班次影响。",
                "Work time = recipe time × native time multiplier ÷ work speed. Lighting, laboratories and support buildings apply where eligible. An in-progress order uses its remaining time. A cycle has up to 600 game seconds of work, with actual throughput affected by deliveries, supplies and shifts."));
        AddGuideSection(content, Tr("卫生回水与管网", "Bathroom loops and pipes"),
            Tr("普通抽水马桶每次消耗 5 kg 水，排出约 11.7 kg 污染水。洗手池每次 5 kg 进、5 kg 出。净水器回收厕所和洗手的全部回水时，每次如厕的理论净回水为 6.7 kg，且需过滤介质和溢流。过滤后的净水仍可带病菌。\n" +
                "默认液管满包 10 kg、气管 1 kg，约每游戏秒移动一段。理论流量要和泵、处理机、共用管线及出口能力分别比较，回水入管属于输送。",
                "A standard lavatory uses 5 kg of water and releases about 11.7 kg of polluted water. A sink converts 5 kg per use. Sieving all of that return yields a net 6.7 kg per toilet visit, with filter medium and overflow needed. Germs can pass through the sieve.\n" +
                "Default liquid packets hold 10 kg and gas packets 1 kg and move about one segment per game second. Check pumps, processors, shared lines and outlets. Routing return water is a transfer."));
        AddGuideSection(content, Tr("畜舍繁殖与补栏", "Stable breeding and replacement"),
            Tr("产蛋间隔 = 满槽所需进度 ÷ 每周期生殖进度。终生产蛋 = 向下取整（成年可繁殖周期 ÷ 产蛋间隔）。\n" +
                "房内的蛋计入未来空间占用，可能使动物进入「狭窄」并暂停繁殖。「拥挤」则影响幸福度。补栏提前量约等于孵化时间＋成长期＋运送时间。\n" +
                "未来 10 周期按现有动物分段预测。供料待确认及幼体成年后的估算列为备选，优先看房间状态和实际产蛋。",
                "Egg interval = full fertility range ÷ progress per cycle. Lifetime eggs round down the adult breeding time divided by the egg interval.\n" +
                "Eggs count toward future space use and can cause Cramped, pausing reproduction. Overcrowded affects happiness. Allow incubation, maturation and transport time for replacements.\n" +
                "The ten-cycle forecast starts with the animals currently present. Unconfirmed feed and juvenile output after maturation are conditional."));
        AddGuideSection(content, Tr("热量与蒸汽涡轮", "Heat and steam turbines"),
            Tr("同相升降温的热量变化（kDTU）= 质量（kg）× 比热容（DTU/g·℃）× 温差（℃）。换热还受导热率和接触条件影响。\n" +
                "U60 常规涡轮按蒸汽批次发电。单口稳定供汽 0.4 kg/s 时，每 0.2 秒只进 0.08 kg，低于结算门槛，平均功率上限约 425 W。\n" +
                "以五口吸入 200°C 蒸汽、2 kg/s、95°C 出水为例。蒸汽冷却热流约 2 × 4.179 × 105 = 878 kDTU/s，机体需散热约 4 ＋ 10% × 878 = 92 kDTU/s。",
                "For a single phase, heat change in kDTU = mass in kg × specific heat in DTU/(g·°C) × temperature change in °C. Heat exchange also depends on conductivity and contact.\n" +
                "In U60, steam settles in batches. One inlet at a steady 0.4 kg/s draws 0.08 kg every 0.2 s, below the settlement threshold, so average power is capped near 425 W.\n" +
                "Five inlets at 2 kg/s with 200°C steam and 95°C exhaust remove about 2 × 4.179 × 105 = 878 kDTU/s from the steam. The body needs about 4 + 10% × 878 = 92 kDTU/s of cooling."));
        AddGuideSection(content, Tr("读懂发电和需求", "Reading generation and demand"),
            Tr("涡轮理论值取刷新时的入口工况，堆肥理论值按持续供料与翻堆。" +
                "发电、设备实际获配电量和连续转换质量按游戏结算累计。发电量含原生发电机产出，设备获配电量含电池供电。\n" +
                "每 3 游戏秒确认计量覆盖，明细可展开 60 秒分段和 3 秒结算。上周期缺测时只显示已覆盖区间的小计。",
                "Turbine theory uses inlet conditions at refresh. Compost theory assumes continuous feedstock and turning. " +
                "Generation, delivered energy and continuous converter mass are accumulated when the game settles them. Device supply includes battery delivery.\n" +
                "Coverage is checked every three game seconds. The breakdown shows 60-second bins and three-second settlements. A partial last cycle shows the subtotal over covered intervals."));

        AddGuideSection(content, Tr("电路和蓄电预算", "Circuit and battery budgets"),
            Tr("耗电量（kJ）= 功率（W）× 运行秒数 ÷ 1,000。例如 400 W 运行 75 秒消耗 30 kJ。导线负载按同一时刻的 W 核对，电池缓冲按停发期间的用电量估算。\n" +
                "两台 600 W 设备同时运行 30 秒，周期平均只有 60 W，并发负载却是 1,200 W，超过普通导线的 1,000 W 额定值。实测发电和设备获配电量的差还会受蓄电、漏损和过量发电影响。",
                "Energy in kJ = watts × operating seconds ÷ 1,000. A 400 W load running for 75 seconds uses 30 kJ. Check simultaneous watts for wire load and demand during generation gaps for battery capacity.\n" +
                "Two 600 W devices running together for 30 seconds average 60 W over a cycle, while their simultaneous 1,200 W exceeds a standard wire's 1,000 W rating. Battery changes, losses and overproduction affect the difference between generation and delivered use."));
        AddGuideSection(content, Tr("间歇泉长期产量", "Long-run geyser output"),
            Tr("分析后可查喷发、闲置和休眠计时。无阻塞的长期平均流量 = 喷发流量 × 喷发占比 × 活跃期占比。\n" +
                "缓冲量 ≈ 停供期间的净需求 × 停供时间。例如每周期缺 720 kg，休眠 10 周期，就需约 7.2 吨。入口处理能力按喷发流量规划，用料预算按长期均值规划。\n" +
                "喷发阶段只估本次喷发在本周期内的上限。过压、地格限制和调谐变化会改变产量，地格排放量未计量。",
                "Analyze a geyser to see eruption, idle and dormancy timing. Unobstructed long-run rate = eruption rate × eruption duty fraction × active-period fraction.\n" +
                "Buffer ≈ net demand × outage duration. A shortfall of 720 kg per cycle over 10 dormant cycles takes about 7.2 tonnes. Size intake for burst flow and budget supply from the long-run mean.\n" +
                "The eruption figure bounds the current burst before cycle end. Pressure, tile constraints and tuning affect yield. Actual discharge into tiles is unmetered."));

        var links = Rect("GuideLinks", guideView, Vector2.zero, Vector2.right);
        links.offsetMax = new Vector2(0, ToolbarHeight);
        ConfigureControls(links);
        MakeButton(links, Tr("中文 Wiki", "ONI Wiki"),
            () => Application.OpenURL("https://oxygennotincluded.wiki.gg/zh/wiki"), 126);
        guideView.gameObject.SetActive(false);
    }

    private void AddGuideSection(RectTransform content, string title, string paragraphs)
    {
        AddMessage(content, title, HeaderColor);
        AddMessage(content, paragraphs, TextColor);
    }

    private void UpdateGuide()
    {
        double food = ColonyDemand(ResourceKey.FoodEnergy);
        double oxygen = ColonyDemand(new ResourceKey("Oxygen", FlowUnit.Kilograms));
        guideColony.text = Tr("本星球复制人每周期 · 口粮 ", "Current colony per cycle · Food ") + Number(food) +
            Tr(" kcal · 氧气 ", " kcal · Oxygen ") + Number(oxygen) + " kg";
        string difficulty = GameRecipes.FoodDifficulty();
        if (difficulty.Length > 0)
            guideColony.text += " · " + difficulty;
    }

    private double ColonyDemand(ResourceKey key)
    {
        if (world == null || !world.Resources.TryGetValue(key, out var row))
            return 0;
        return row.Sources.Where(source => source.Key.PrefabId == MinionConfig.ID ||
            source.Key.PrefabId == BionicMinionConfig.ID).Sum(source => source.Value.Consumed);
    }
}
