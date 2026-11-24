using SandUp.Domain;

namespace SandUp.Desktop;

internal sealed record BodyPartGuideEntry(string Risk, string Activity, string Evidence);

internal static class BodyPartGuide
{
    public static BodyPartGuideEntry For(BodyPart part) => part switch
    {
        BodyPart.Eyes => new(
            "持续近距离看屏幕可能出现数字视疲劳或干眼症状，例如眼睛干涩、酸胀、短暂视物模糊。",
            "把视线移到窗外或房间远端，放松看约 20 秒，再慢慢眨眼几次。",
            "美国国立卫生研究院用眼建议，2024 · Talens-Estarelles 等，2023"),
        BodyPart.Shoulders => new(
            "长时间保持同一姿势可能伴随肩颈痛、僵硬，尤其容易在继续伏案时感到不适。",
            "松开鼠标，放下双肩。缓慢向左、向右转头各两次，在舒服的幅度内活动，疼痛加重就停。",
            "英国 NHS 颈痛资料 · 办公人群活动休息试验，2021"),
        BodyPart.Back => new(
            "久坐不换姿势时，可能出现腰痛或背部僵硬。仅凭坐了多久，无法判断是否有椎间盘疾病。",
            "慢慢起身，按舒服的步速走 1–2 分钟。暂时起不了身时，换个坐姿，让靠背托住腰背。",
            "英国 NHS《背痛》 · 办公人群活动休息试验，2021"),
        BodyPart.Abdomen => new(
            "长期久坐与2型糖尿病风险升高有关。观察研究也发现，较少打断久坐的人群腰围往往更大。",
            "起身走动约 2 分钟，比如走到门口再回来。这个动作是为了打断久坐，当天的分数无法衡量腹脂变化。",
            "世卫组织久坐指南，2020 · Healy 等，2008 · Dunstan 等，2012"),
        BodyPart.Legs => new(
            "坐久了可能感到臀部受压、腿部发僵。小型实验还观察到，连续坐几小时后，部分下肢血管的舒张反应会减弱。",
            "离开椅面，轻松走动约 2 分钟。不便起身时，调整坐姿，缓慢抬起脚尖再放下几次。",
            "办公座椅压力研究，Arippa 等，2022 · 下肢血管实验，Thosar 等，2015"),
        BodyPart.Pelvic => new(
            "目前证据不足以认定普通办公久坐会引起痔。",
            "换个舒适姿势，方便时起身走动 1–2 分钟。",
            "Peery 等痔病研究，2015 · 美国结直肠外科医师学会痔病指南，2024"),
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
}
