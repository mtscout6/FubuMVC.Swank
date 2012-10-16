﻿using FubuMVC.Swank.Description;
using NUnit.Framework;
using Should;

namespace Tests.Description
{
    [TestFixture]
    public class OptionConventionTests
    {
        private OptionConvention _optionConvention;

        [SetUp]
        public void Setup()
        {
            _optionConvention = new OptionConvention();
        }

        public enum Options
        {
            [FubuMVC.Swank.Description.Description("Option 2", "This is option 2.")]
            Option2,
            Option1
        }

        [Test]
        public void should_return_default_description_of_option()
        {
            var description = _optionConvention.GetDescription(typeof(Options).GetField("Option1"));
            description.Name.ShouldBeNull();
            description.Comments.ShouldBeNull();
        }

        [Test]
        public void should_return_attribute_description_of_option()
        {
            var description = _optionConvention.GetDescription(typeof(Options).GetField("Option2"));
            description.Name.ShouldEqual("Option 2");
            description.Comments.ShouldEqual("This is option 2.");
        }
    }
}