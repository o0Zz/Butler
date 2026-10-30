namespace Butler.Tests;

public class SkillsTests
{
    [Fact]
    public void Lists_skill_folders_with_their_front_matter()
    {
        var dir = Directory.CreateTempSubdirectory("skills-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "triage-mail"));
            File.WriteAllText(Path.Combine(dir, "triage-mail", "SKILL.md"), "---\r\nname: triage-mail\r\ndescription: Turn alert mails into tickets.\r\n---\r\n\r\n# Body\r\nname: not this\r\n");
            Directory.CreateDirectory(Path.Combine(dir, "bare"));
            File.WriteAllText(Path.Combine(dir, "bare", "SKILL.md"), "# no front matter");
            Directory.CreateDirectory(Path.Combine(dir, "not-a-skill"));
            File.WriteAllText(Path.Combine(dir, "butler.json"), "[]");

            Assert.Equal([new Skill("bare", "", "bare"), new Skill("triage-mail", "Turn alert mails into tickets.", "triage-mail")], Skills.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("/triage-mail", "triage-mail")]
    [InlineData("/timesheet fill the month", "timesheet")]
    [InlineData("/anthropic-skills:docx make it", "anthropic-skills:docx")]
    [InlineData("Check my mails", null)]
    public void Finds_the_invoked_skill(string prompt, string? skill) => Assert.Equal(skill, Skills.Invoked(prompt));
}
