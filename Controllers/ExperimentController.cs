using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace DigitalTwinSync.Experiment.Controllers;

[ApiController]
[Route("api/experiment")]
public sealed class ExperimentController : ControllerBase
{
    private readonly ExperimentRunner _runner;

    public ExperimentController(ExperimentRunner runner)
    {
        _runner = runner;
    }

    [HttpGet("defaults")]
    [ProducesResponseType<ExperimentRequest>(StatusCodes.Status200OK)]
    public ActionResult<ExperimentRequest> GetDefaults()
    {
        return Ok(new ExperimentRequest());
    }

    [HttpGet("run-default")]
    [ProducesResponseType<ExperimentReport>(StatusCodes.Status200OK)]
    public Task<ActionResult<ExperimentReport>> RunDefault(CancellationToken cancellationToken)
    {
        return RunExperiment(new ExperimentRequest(), cancellationToken);
    }

    [HttpPost("run")]
    [ProducesResponseType<ExperimentReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<ExperimentReport>> Run(
        [FromBody] ExperimentRequest request,
        CancellationToken cancellationToken)
    {
        return RunExperiment(request, cancellationToken);
    }
    
    private async Task<ActionResult<ExperimentReport>> RunExperiment(
        ExperimentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = await Task.Run(() => _runner.Run(request), cancellationToken);
            return Ok(report);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }
}
