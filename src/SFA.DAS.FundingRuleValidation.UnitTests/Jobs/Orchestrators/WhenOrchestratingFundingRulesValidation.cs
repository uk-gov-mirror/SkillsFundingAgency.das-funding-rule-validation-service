using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Testing;
using SFA.DAS.FundingRuleValidation.Jobs.Activities;
using SFA.DAS.FundingRuleValidation.Jobs.Domain;
using SFA.DAS.FundingRuleValidation.Jobs.Orchestrators;

namespace SFA.DAS.FundingRuleValidation.UnitTests.Jobs.Orchestrators;

public class WhenOrchestratingFundingRulesValidation
{
    private const string InstanceId = "777";
    private Mock<TaskOrchestrationContext> _context;
    private FakeLogger<FundingRuleOrchestrator> _fakeLogger;
    
    [SetUp]
    public void Setup()
    {
        _context = new Mock<TaskOrchestrationContext>();
        _fakeLogger = new FakeLogger<FundingRuleOrchestrator>();
        _context
            .Setup(x => x.CreateReplaySafeLogger(nameof(FundingRuleOrchestrator.ApplyFundingRules)))
            .Returns(_fakeLogger);
        _context
            .Setup(x => x.InstanceId)
            .Returns(InstanceId);
    }
    
    [Test, MoqAutoData]
    public async Task Then_When_There_Are_No_Rules_The_Request_Passes_Validation()
    {
        // arrange
        var command = new ValidateLearnerCommand(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "123456789", "987654321", []);
        
        _context
            .Setup(x => x.GetInput<ValidateLearnerCommand>())
            .Returns(command);
        
        _context
            .Setup(x => x.CallActivityAsync<List<FundingRule>>(nameof(GetActiveRulesForDatesActivity), It.IsAny<List<DateTime>>(), It.IsAny<TaskOptions?>()))
            .ReturnsAsync([]);
        
        // act
        await FundingRuleOrchestrator.ApplyFundingRules(_context.Object);

        // assert
        _context.Verify(x => x.CallActivityAsync<RuleCourseOutcome>(It.IsAny<TaskName>(), It.IsAny<RuleData>(), It.IsAny<TaskOptions?>()), Times.Never);
        _context.Verify(x => x.CallActivityAsync(
            nameof(SendValidationResultActivity),
            It.Is<ValidateLearnerResult>(r => r.Status == ValidationStatus.Passed && !r.RuleOutcomes.Any()), 
            It.IsAny<TaskOptions?>()), Times.Once);
    }
    
     [Test, MoqAutoData]
     public async Task Then_Passing_Rule_Outcomes_Are_Returned(
         List<FundingRule> rules,
         Course course)
     {
         // arrange
         var ruleNames = rules.Select(x => x.IlrRuleName).ToHashSet();
         course.StartDate = DateTime.UtcNow;
         var command = new ValidateLearnerCommand(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "123456789", "987654321", [course]);
         
         _context
             .Setup(x => x.CallActivityAsync<List<FundingRule>>(nameof(GetActiveRulesForDatesActivity), It.IsAny<List<DateTime>>(), It.IsAny<TaskOptions?>()))
             .ReturnsAsync(rules, delay: TimeSpan.FromMilliseconds(100));
     
         _context
             .Setup(x => x.GetInput<ValidateLearnerCommand>())
             .Returns(command);
         
         _context
             .Setup(x => x.CurrentUtcDateTime)
             .Returns(() => DateTime.UtcNow);
     
         foreach (var rule in rules)
         {
             rule.EffectiveFrom = DateTime.UtcNow.AddDays(-10);
             rule.EffectiveTo = DateTime.UtcNow.AddDays(10);
             rule.CourseIds = command.Courses.Select(x => x.Id).ToHashSet();
             _context
                 .Setup(x => x.CallActivityAsync<List<RuleCourseOutcome>>(rule.RuleName, It.IsAny<RuleData>(), It.IsAny<TaskOptions?>()))
                 .ReturnsAsync([
                     new RuleCourseOutcome(
                         rule.Id,
                         rule.IlrRuleName,
                         rule.IlrRuleDescription,
                         command.Courses.First().Id,
                         command.Courses.First().AimSequenceNumber,
                         RuleOutcome.Success,
                         [])
                 ]);
         }

         ValidateLearnerResult? capturedResult = null;
         _context
             .Setup(x => x.CallActivityAsync(nameof(SendValidationResultActivity), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
             .Callback<TaskName, object?, TaskOptions?>((_, x, _) => capturedResult = x as ValidateLearnerResult)
             .Returns(value: Task.CompletedTask);
     
         // act
         await FundingRuleOrchestrator.ApplyFundingRules(_context.Object);
     
         // assert
         capturedResult.Should().NotBeNull();
         capturedResult!.CorrelationId.Should().Be(command.CorrelationId);
         capturedResult.Ukprn.Should().Be(command.Ukprn);
         capturedResult.Uln.Should().Be(command.Uln);
         capturedResult.Status.Should().Be(ValidationStatus.Passed);
         capturedResult.RuleOutcomes.Should().HaveCount(3);
         capturedResult.RuleOutcomes.Should().AllSatisfy(x =>
         {
             ruleNames.Contains(x.RuleName).Should().BeTrue();
         });
         
         _fakeLogger.LatestRecord.Message.Should().Be("Learner validation complete");
        
         // first scope
         var scope = _fakeLogger.LatestRecord.Scopes[0] as Dictionary<string, string>;
         scope.Should().ContainEquivalentOf(new KeyValuePair<string, string>("CorrelationId", command.CorrelationId));
         scope.Should().ContainEquivalentOf(new KeyValuePair<string, string>("WaitingInstanceId", command.WaitingInstanceId));
        
         // second scope
         scope = _fakeLogger.LatestRecord.Scopes[1] as Dictionary<string, string>;
         scope.Should().ContainKey("Duration");
         var duration = TimeSpan.Parse(scope["Duration"]);
         duration.Should().BeCloseTo(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100));
     }
     
     [Test, MoqAutoData]
     public async Task Then_Failing_Rule_Outcomes_Are_Returned(
         List<FundingRule> rules,
         Course course,
         Mock<TaskOrchestrationContext> context)
     {
         // arrange
         var ruleNames = rules.Select(x => x.IlrRuleName).ToHashSet();
         course.StartDate = DateTime.UtcNow;
         var command = new ValidateLearnerCommand(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "123456789", "987654321", [course]);
         
         context
             .Setup(x => x.CallActivityAsync<List<FundingRule>>(nameof(GetActiveRulesForDatesActivity), It.IsAny<List<DateTime>>(), It.IsAny<TaskOptions?>()))
             .ReturnsAsync(rules);
     
         context
             .Setup(x => x.GetInput<ValidateLearnerCommand>())
             .Returns(command);
     
         foreach (var rule in rules)
         {
             rule.EffectiveFrom = DateTime.UtcNow.AddDays(-10);
             rule.EffectiveTo = DateTime.UtcNow.AddDays(10);
             rule.CourseIds = command.Courses.Select(x => x.Id).ToHashSet();
             context
                 .Setup(x => x.CallActivityAsync<List<RuleCourseOutcome>>(rule.RuleName, It.IsAny<RuleData>(), It.IsAny<TaskOptions?>()))
                 .ReturnsAsync([new RuleCourseOutcome(
                     rule.Id,
                     rule.IlrRuleName,
                     rule.IlrRuleDescription,
                     command.Courses.First().Id,
                     command.Courses.First().AimSequenceNumber,
                     RuleOutcome.Error,
                     [new FundingRestriction("RestrictionName", "RestrictionType")])]);
         }

         ValidateLearnerResult? capturedResult = null;
         context
             .Setup(x => x.CallActivityAsync(nameof(SendValidationResultActivity), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
             .Callback<TaskName, object?, TaskOptions?>((_, x, _) => capturedResult = x as ValidateLearnerResult)
             .Returns(Task.CompletedTask);
     
         // act
         await FundingRuleOrchestrator.ApplyFundingRules(context.Object);
     
         // assert
         capturedResult.Should().NotBeNull();
         capturedResult!.CorrelationId.Should().Be(command.CorrelationId);
         capturedResult.Ukprn.Should().Be(command.Ukprn);
         capturedResult.Uln.Should().Be(command.Uln);
         capturedResult.Status.Should().Be(ValidationStatus.Failed);
         capturedResult.RuleOutcomes.Should().HaveCount(3);
         capturedResult.RuleOutcomes.Should().AllSatisfy(x =>
         {
             ruleNames.Contains(x.RuleName).Should().BeTrue();
         });
         
         var fundingRestrictions = capturedResult.RuleOutcomes.SelectMany(x => x.FundingRestrictions);
         fundingRestrictions.Should().HaveCount(3);
     }

     [Test, MoqAutoData]
     public async Task Then_Failed_Activity_Invocations_Are_Caught_And_Return_SystemError(
         List<FundingRule> rules,
         Course course)
     {
         // arrange
         course.StartDate = DateTime.UtcNow;
         var command = new ValidateLearnerCommand(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "123456789", "987654321", [course]);
         
         _context
             .Setup(x => x.CallActivityAsync<List<FundingRule>>(nameof(GetActiveRulesForDatesActivity), It.IsAny<List<DateTime>>(), It.IsAny<TaskOptions?>()))
             .ReturnsAsync(rules);
     
         _context
             .Setup(x => x.GetInput<ValidateLearnerCommand>())
             .Returns(command);
     
         foreach (var rule in rules)
         {
             rule.EffectiveFrom = DateTime.UtcNow.AddDays(-10);
             rule.EffectiveTo = DateTime.UtcNow.AddDays(10);
             rule.CourseIds = command.Courses.Select(x => x.Id).ToHashSet();
             _context
                 .Setup(x => x.CallActivityAsync<List<RuleCourseOutcome>>(rule.RuleName, It.IsAny<RuleData>(), It.IsAny<TaskOptions?>()))
                 .ThrowsAsync(new TaskFailedException(rule.RuleName, 100, new Exception("Failed to run activity")));
         }

         ValidateLearnerResult? capturedResult = null;
         _context
             .Setup(x => x.CallActivityAsync(nameof(SendValidationResultActivity), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
             .Callback<TaskName, object?, TaskOptions?>((_, x, _) => capturedResult = x as ValidateLearnerResult)
             .Returns(Task.CompletedTask);
     
         // act
         await FundingRuleOrchestrator.ApplyFundingRules(_context.Object);
     
         // assert
         capturedResult.Should().NotBeNull();
         capturedResult!.CorrelationId.Should().Be(command.CorrelationId);
         capturedResult.Ukprn.Should().Be(command.Ukprn);
         capturedResult.Uln.Should().Be(command.Uln);
         capturedResult.Status.Should().Be(ValidationStatus.SystemError);
         capturedResult.RuleOutcomes.Should().HaveCount(0);
         
         _fakeLogger.Collector.GetSnapshot()[^2].Message.Should().Be("Learner validation failed");
     }
}