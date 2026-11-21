using System.Reflection;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// PaintSettings.Clone is written out by hand (s&box forbids MemberwiseClone), so it is easy to forget a
    /// field when one is added. This test changes every public field and checks the copy carries every change.
    /// </summary>
    public class PaintSettingsCloneTests
    {
        [Fact]
        public void Clone_copies_every_public_field()
        {
            var original = new PaintSettings();
            FieldInfo[] fields = typeof(PaintSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Assert.NotEmpty(fields);

            // Give every field a value that differs from its default and from every other field.
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f.FieldType == typeof(float)) f.SetValue(original, (float)f.GetValue(original) + 0.125f * (i + 1));
                else if (f.FieldType == typeof(int)) f.SetValue(original, (int)f.GetValue(original) + i + 1);
                else if (f.FieldType == typeof(bool)) f.SetValue(original, !(bool)f.GetValue(original));
                else Assert.Fail("PaintSettings." + f.Name + " has a type this test does not know how to change: " + f.FieldType.Name);
            }

            PaintSettings copy = original.Clone();

            Assert.NotSame(original, copy);
            foreach (FieldInfo f in fields)
                Assert.True(Equals(f.GetValue(original), f.GetValue(copy)), "Clone() does not copy PaintSettings." + f.Name);
        }

        [Fact]
        public void Changing_the_copy_leaves_the_original_alone()
        {
            var original = new PaintSettings();
            PaintSettings copy = original.Clone();
            copy.CurtainStrength = 0.25f;
            copy.MaxDrips = 7;
            Assert.Equal(1f, original.CurtainStrength);
            Assert.NotEqual(7, original.MaxDrips);
        }
    }
}
