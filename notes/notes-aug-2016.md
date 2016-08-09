# Some Notes on Using Machine Learning to Develop Inlining Heuristics

August 2016

## Overview

This document describes the work done from roughly February to August
2016 to use machine learning techniques to develop improved inlining
heuristics for RyuJit. 

Based on this work, RyuJit now includes an inlining heuristic that is
based on machine learning -- the ModelPolicy. This policy can be
enabled by setting COMPlus_JitInlinePolicyModel=1 in environments
where the jit generates code. Measurements on various internal
benchmarks have shown this new policy gives roughly 2% geomean CQ
improvement, 2% geomean CS reduction, and 1% througput reduction.
Measurements on "realistic" applications has just begun and the
initial results are not as encouraging, but we are still optimistic
that with some more work, the ModelPolicy or something quite similar
can be enabled as the default policy going forward.

A number of new measurement techniques were developed to support the
modelling process. Even so, the models built so far are not entirely
satisfactory. There are significant challenges and open questions
in many areas of the work.

The remainder of this aims to describe the work that has been done,
present the challenges that remain, and suggest avenues for further
investigation. Note this is still a work in progress and some aspects
of it are incomplete.

## Background

The desirability of a machine-learning approach to the development of
inlining heuristics was based on both past experience and some
promising results from the literature.

Past experience in manual development of inlining heuristics has shown
that it is a complex and challenging endeavor. Typically the heuristic
developer must carefully study some number of examples to try and
discern what factors lead to "good" inlines. These factors are then
coded as heuristics, and combined via some ad-hoc method (say, via
weights) to produce an overall figure of merit.  A large number of
rounds of experimental tuning on benchmarks are then used to select
weight values. 

Failure of the heuristic to perform on certain benchmarks can and
perhaps should lead to refining existing heuristics or the development
of new heuristics, or to the improvement of downstream optimization
abilities in the compiler, but often instead is handled by adjusting
the the various weights to try and obtain the desired outcome. There
is inevitable bias in the developer's choice of factors and the expert
analysis required to gain insight only scales to relatively small
numbers of examples. Rigorous analysis to cross-check the importance
of factors is not always done and performance of the model over time
is typically not measured. This can lead to misleading confidence in
the heuristics, since benchmark program never change, while real
applications evolve over time, sometimes quite rapidly.

The recent literature has describes some successes in using machine
learning to create good inlining heuristics. One example is [Automatic
Construction of Inlining Heuristics using Machine
Learning](http://dl.acm.org/citation.cfm?id=2495914) by Kulkarni,
Cavazos, Wimmer, and Simon. Here Kulkarni et. al. treat inline
profitability as an unsupervised learning problem, and create a
well-performing heuristic black box (neural network) using
evolutionary programming techniques. They then turn around and use
this black box as an oracle to label inline instances, and from this
guide a supervised machine learning algorithm to produce a decision
tree that expresses the profitability heuristics in terms sensible to
the compiler writer.

It was hoped that machine learning techniques would lead to decent
models that could be created relatively quickly, so that new models
could be developed as the jit was ported to new architectures and new
operating systems. Also as the capabilities of the jit or runtime were
extended (say by improving register allocation or optimization) it
would be possible to quickly re-tune the inliner to take best
advantage of new capabilities, and/or to validate continued good
behavior as key applications evolve. These tasks remain within the
scope of our ambition, though we have not yet proven that such things
are possible.

Our inability (described in more detail below) to easily derive good
performance models based on machine learning is most likely an
indictment of some aspects of our overall process, though it is also
possible that our difficulties simply reflect the degree of challenge
inherent in improving heuristics in a mature and complex system with
various realistic constraints.

This [initial design
note](https://github.com/dotnet/coreclr/blob/master/Documentation/design-docs/inlining-plans.md)
-- describing the early views on the project -- may be of interest.

## Motivation

The primary motivation for working on inlining was the potential for
improved code quality (CQ) at similar or improved levels of code size
(CS) and jit throughput (TP).

This potential had been observed in many manual examples and bug
reports, as well as experiments to simply make the inliner more
aggressive.

Nominal upside in CQ, given the current optimization capabilities of
the jit, is in the range of 3-4% (geomean) across a variety of
programs.  As is always the case with such measures, the underlying
distribution is broad, with some programs speeding up by substantially
more, many remaining about the same, an a few slowing down.

CQ generally increases steadily in aggregate with more inlining. For
reasonable amounts of inlining, the cases where inlining hurts
performance are fairly rare. At high enough levels of inlining there
may be adverse interations as optimizer thresholds are tripped, and
eventually the impact of the larger code is felt as contention for the
limited physical memory resources of the host machine.

CS (and TP) are often though of as constraint or penalty terms rather
than as optimization objectives. It is clear from experiments that
inlining of suitably small methods will decrease CS and TP, so
obtaining the "minimal" value for these metrics requires some amount
of inlining. Too much inlining will increase CS without providing
improvements in CQ.

So, for a given level of CQ, there is a range of CS values that can
obtain that CQ. The "ideal" level is then the minimal CS needed;
roughly speaking, there is a CS/CQ tradeoff region with a bounding
curve at the minimum CQ level. The locus and shape of this curve is
unknown and must be discovered empirically. The curve will also vary
considerably depending on the benchmarks. Ensemble measures of
performance are necessary, and (as noted above) when comparing
two well-performing heuristics, there will be always be examples
where one heuristic outperforms the other.

Various system design goals and runtime capabilities (eg desire for
fast startup or good steady-state performance or blend of both,
ability to dynamically re-optimize) dictate which regions of the curve
is most desirable. The challenge, then, is to develop an inlining
heuristic that picks out an appropriate point that lies on or near the
tradeoff curve given the design goals. The shape of the tradeoff
curve is also of interest.

In our case the ambition is to build a new inlining heuristic that can
increase CQ to get at as much of the headroom as practical, while
decreasing CS and TP.

## History 

The work done so far proceeded in roughly 4 stages, in order:
refactoring, size measurements and modelling, time measurements and
modelling, and speed measurements and modelling. These are described
briefly below and in more detail in subsequent sections.

Refactoring was done to enable the jit to have mulitple inlining
policies that could exist side by side. For compatibility reasons it
was desirable to presenve the exsting (legacy) behavior, and allowing
other policies side by side facilitates experimentation. The legacy
inliner's decision making was intertwined with the observation
process, so it was necessary to separate these out.

Size impact of inlining was measured using the "crossgen" feature of
the CLR. Here the jit is asked to generate code for most of the
methods in an assembly ahead of time. The size impact of each inline
was recorded along with the various observational values that were
available to feed into a heuristic. This data fed into a size model
that produced a size estimating heuristic. The models developed so
far seem reasonbly accurate, with an R^2 value of around 0.6.

The time impact of inlining was measured by capturing CPU cycles
expended in the jit between the time inlining had finished an the time
the native code was generated (notably, this omits the time spent
inlining, which is more difficult to meaure). Modelling showed this
time was closely related to the overall emitted size of the method,
which was shown to be fairly reliably estimated by the sum of an
initial time estimate plus the size impact of each successive inline.

The performance impact of inlienes was measured by enabling hardware
performance monitoring counters to capture the number of instructions
retired as the jitted code ran. Inlines were measured in isolation,
one by one, and the difference in instructions retired was attributed
to the inline. This data along with observations formed the data set
that feed the speed model. Unfortunately, this data has proven to be
difficult to model accurately.

## Constraints and Assumptions

The CoreCLR currently gives its jit one opportunity to generate code
for a method. The time it takes the jit to generate native code is a
concern (eg it potentially impacts application start-up time), and
given the general size-time relationship, this limits the ability of
the jit to inline aggressively or to perform deep analysis in an
attempt to find an optimal set of inlines for a method. The jit also
has very limited ability to convey knowledge from one invocation to
the next, so analysis costs cannot effectively be amortized.

Currently the jit walks it is IR in linear fashion deciding whether to
inline each time it sees a candidate. If the decision is yes then the
inlined code is spliced in place of the call and (because of the order
of the walk) immediately scanned for inlining candidates. Thus the
inlining is performed "depth first" and is done without much knowledge
of the number or location of other candidates in the code
stream. Inlining is done very early on before any significant analysis
has been done to the IR -- there is a flow graph but no loop nesting,
dataflow, or profile estimates are generally available.

Thus the heuristic we have in mind is one that assesses each inline
independent of any assessments done before. Factors visible at the
immediate call site and some general information about the accumulated
IR can be used to influence decisions, so it's possible given a method
A with two callsites for to B that one call to B gets inlined and the
other doesn't.

## Overall Approach to Heuristic Creation

The work history above reflects the initial proposal for heuristic
creation -- first build size and speed models, and then combine those
to create a heuristic. The general idea was to have an explicit
size/speed tradeoff made per inline. The idealized heuristic is:
```
  if (SizeDelta <= 0) { inline; }
  else if (SpeedDelta > alpha * SizeDelta) { inline; } 
``` 
where here SizeDelta represents the increase in code size caused by
the inline, SpeedDelta is the decrease in instructions executed, and
alpha is a tradeoff factor. So good inlines either decrease size, or
justify their size increase with a speed decreasse, and alpha
describes how willing we are to trade speed for size.

This is roughly the heuristic implemented by the ModelPolicy.
SizeDelta and SpeedDelta are computed by models derived from machine
learning, alpha is manually chosen by "tuning" to give the desired
tradeoff.

However the implemented model has an additional parameter, one whose
presence reflects one of the key challenges present in this work. The
size model has natural units of bytes of code (or instructions, if
they're fixed size). Size impacts from inlining are typically small,
say in the range of a few hundred bytes one way or the other.  But the
speed impact of an inline can vary over a much wider range. If we
measure the actual change in instructions retired on a benchmark given
one inline difference, the value may vary from -1e9 to 1e9 with many
values clustered closely around zero.

In an attempt to pull these values into a more manageable range for
modelling, the value provided by the model is instructions retired per
call to the callee. This needs to be multiplied by a "call site
weight" beta to reflect the importance of the call site to the caller,
and further by some "root method weight" to reflect the importance of
the root method to the overall benchmark. We currntly use ad-hoc
methods to estimate beta and ignore the root method weight, so the
full heuristic is:
```
  if (SizeDelta <= 0) { inline; }
  else if (beta * PerCallSpeedDelta > alpha * SizeDelta) { inline; } 
```
Here beta can vary depending on call site, and alpha is the fixed
size-speed tradeoff.

One might legitimately question this model, even if all of the
quantities could be estimated perfectly. More on this subsequently.

## Some Terminology

An *inline tree* is the set of inlines done into a method. The root
method is the initial method; the top level inlines are the
descendants of the root, and so on.

An *inline forest* is the set of inline trees that are in effect for a
benchmark run. There is one inline tree for each method executed.

A inline tree X is a *subtree* of an inline tree Y if Y contains all
the inlines in X and possibly more. A tree X is a *proper parent* of Y
if Y contains just one extra inline.

## Size Modelling and Measurements

### Size Measurements

To measure size, the legacy inliner was modified so that in each
method, it would stop inlining after some number of inlines, K, where
K could be specified externally. The jit would then generate native
code for each method and measure the methods' native code size.  Since
the inlining decisions made in each method jitted are independent,
data from many inlining instances can be collected in one run of
crossgen, potentially one per "root" method. 

The overall process ran from K = 0 up to Kmax. For each run the
size of the method was dumped to a file along with various
observational values that were available to feed into a heuristic, and
various metadata used to identify the root method. For each row of
data, the value of K was recorded as the "version" of the inlining
experiment.

Given the raw data, the native size impact of each inline can then be
determined by a post-processing pass: for each method and each inline
into the method, the size change is found by subtracting the method
size for case where J-1 inlines were performed from the size when J
inlines were perfomed. Note not all methods will be able to perform
the full set of K inlines, so as K increases, the number of methods
that do more inlines decrease. So if there are initially N root
methods the total number of rows of inline data with a given version
decreases as the version increases.

Reliably identifying the root across runs proved nontrivial, since the
main values used as identifying keys (token and hash) were not
sufficiently unique. These might come from addtional stub methods
created by the crossgen process or perhaps from multipliy instantiated
generic methods. Postprocessing would thus ignore any data from a
method where the key was not unique (eg multiple version 0 rows with
the same token and hash).

The [data set used](https://...) to develop the current model is taken
from a crossgen of the CoreCLR core library. It has 29854 rows. Given
the special role played by this library it is quite possible this is
not a good representative set of methods. Considerably more and more
diverse data was gathered (upwards of 1M rows using the desktop "SPMI"
method) but this data proved unwieldy. The data gathered is also
specific to x64 and windows and the behavior of the jit at that time.

Subsequent work on performance measurement has created new data sets
that could be used for size modelling, since a similar sort of
K-limiting approach was used for performance, and the factor
observations for size and speed are common. The most recent such data
set is the [v12 data](https://...).

### Size Modelling

The size data is "noise free" in that (absent errors in coding) the
sizes in the data set should be completely accurate.  Given the
relatively simple behavior of the jit it was felt that a linear model
should work well.

The model needs to be relatively simple to implement and quick to
evaluate, and it is highly desirable that it be interpretable. Based
on this the model developed is a penalized linear model using R's
'glmnet'. [This script](http://...) was used to derive the model.  It
is implemented by `DiscretionaryPolicy::EstimateCodeSize` in the code
base. This model explains about 55% of the variance in the mscorlib
size data, and 65% of the variance gained in the v12 data.

Naive use of more sophisticated models (eg random forests, gradient
boosting, mars) to see how much the linear model might be leaving
behind didn't yield much improvement.

So the belief is (given the noise-free measurements that can be made
for code size) that the remaining unexplained variance comes from
missing observations. An exploration of poorly fitting examples would
likely prove fruitful. There is likely some nontrivial amount of
variation that will never be easily explained -- the jit's code
generation can be quite sensitive to the exact details of both root
method and callee.

Exactly how close one can come to modelling size is an open question.

The degree to which the inaccuracy of the current size model hurts the
overall inline heuristic performance is another. The belief is that
the speed and high-level structure of the heuristic are likely larger
contributors to poor performance. However, they may also be more
difficult to improve.

### Size Model Viewed as Classification

Given the form of the idealized heuristic, drawing a clear distinction
between size-increasing and non-size-increasing inlines is
important. We can view the regression model developed above as a
classifier and see how well it performs at this task (here on the V12
data):

               | Est Decrease | Est Increase | Total
---------------|--------------|--------------|-------
Size Decrease  | 2052         | 776          | 2828
Size Increase  | 132          | 3162         | 3298
Total          | 2188         | 3938         | 6126

So the model is quite accurate in predicting size increasing cases,
getting only 132/3298 wrong (96% accuracy).

It's not as good at predicting size decreasing cases: 776/2828 were
misclassified as size increasing (72% accuracy).

To better implement the idealized heuristic, it might make sense to
bias the model to increase the accuracy of classifying size decreasing
cases. For instance, setting the classification threshold to EstSize -
40 (recall value is in bytes * 10) would give roughly balanced error
rates.  The downside is that a larger number of size increasing cases
are now inlined without further scrutiny.

For inlines classified as size increasing, the magnitude of the size
comes into play, so one might also attempt to make more accurate
predictions for size increasing inlines and trade off accuracy in
predicting the magnitude of size decreases.

## Speed Model and Measurements

### Speed Measurements

While noise-free size measurements are easy to come by, some degree of
noise is inevitable for most approaches to speed measurements.
Generally speaking any inline whose impact is of the same magnitude as
the ambient noise level will very difficult to measure.

The most common approach is to measure wall-clock or process time or
cycle time for a benchmark. It is difficult to get noise levels for
these approaches below roughly 1% of the overall runtime of the
test. This amount of noise restricts the set of measurable
inlines. Aside from interference by other processes running on the
host machine, time-based measurements also can fall prey to
microarchitectural implementation issues, in particular things like
loop alignments, global branch prediction, various security-inspired
randomization techniques, power management, and so on. Thus even
run-to-run repeatabilty on an otherwise quite machine will be
impacted. The inliner also operates early enough in the compilation
pipleline that machine microarchitecture is of a secondary concern.

To avoid some of these pitfalls we have adopted the instructions
retired by the benchmark as our primary performance metric. This is
relatively insensitve to microarchitectural detals (with some caveats)
and noise levels of 0.01% - 0.1% are not difficult to come by.

Measuring instructions retired on Windows requires elevation since
only the kernel can access and program the performance monitoring
counters (PMC).

### Isolating Inlines

To capture the per-inline impact on performance one must be able to
run the benchmark twice, varying just one inline between the two runs.
To do this requires some care. The K-limiting approach used during the
size data collection does not sufficiently control inlining.

So instead we developed some alternate techniques. One of them is to
use K-limiting along with the ability to suppress inlining in all but
one root method and a FullPolicy inline heuristic that inlines where
possible. This combination allows successive measurements where just
one inline differs (with some care taken to handle for force inlines).
Note the "context" of the inline is the one that arises from the DFS
enumeration. So as K increases we may be inlining deep into the tree.

Because we wanted a greater quantity of samples for shallow inlines we
developed a second technique where inline replay is used to carefully
control inlining. An inline forest was grown by enabling some an
inline policy and collecting up the initial inline forest. Inlining
for each root in the forest was then disabled and a new run was done;
this collects forests for new roots (methods that were always inlined
in the initial run). This process continues until closure, yielding a
full forest for that policy.

This full forest is expressed in inline XML. Inline replay can then be
used to isolate inlines to just one tree in the forest by measuring
the performance of a tree and one of its proper parents.  In actuality
we measured each tree by growing from the empty tree towards the full
tree in a breadth-first fashion. It is probably a good idea to try a
greater variety of exploration orders here.

As an aside, using an aggressive policy like the FullPolicy, one can
enumerate the "jit-visible" call graph, a graph that shows the range
of possible inline trees and hence inline forests.

### How to Measure an Inline

The measurement process described below is orchestrated by the
[PerformanceEXplorer](http://...).

Benchmarks are set up to run under xunit-performance. Per-benchmark
times are rougly normalized to 1 second to try and keep the variance
constant in each benchmark.

Xunit-performance runs the benchmark code via reflection. For each
attributed method it runs some number of iterations, enabling
performance counting via ETW (on windows). It also issues events for
the start and end of each benchmark and each iteration of the
benchmark.  The event stream is post processed to find events
attributed to the benchmark process that fall within the iteration
span of a particular benchmark method. These events are counted and
the total is multiplied by the PMC reload interval (100,000 I believe)
to give the overall instruction retired estimate for that iteration.
The raw iteration data is then written to an XML file. This data is
read by the orchestrating process and an average count is computed
from the iterations. This average value is then subtracted from the
averaged value measured by in the proper parent run to get the
per-inline performance impact.

The overall exploration process repeats the above for each root in
each benchmark, growing trees from their noinline version up to the
full version seen in the forest.  Exploration is short-circuited for a
root if the full tree performance for that root does not differ
significantly from the noinline version, or if the root has no
inlines. Roots are explored in the order of the number of calls (see
below).

### Addtional Data Measured

Along with the measured change in instructions retired, it seemed
important to also get some idea about how the call counts were
changing in the program -- in particular how often the root being
explored was called, and how frequently it called the method being
inlined. To do this a special instrumentation mode was developed that
hijacks the IBC mechanism present in the CoreCLR. each jitted method's
entry was instrumented with a counter to count the number of
calls. These counts are dumped on jit shutdown. The root method count
is directly available; the callee count can be deduced by knowing its
value in the noinline run and accounting for any other inlines of that
callee made along the way.

One failing of this method is that if the callee comes from a
prejitted image it will never run in instrumented form. To work around
this the use of prejitted images can be disabled. This creates its own
set of complications because every benchmark contains a sizeable
amount of startup code that might be repeatedly explored. So
optionally the explorer maintains a list of already explored methods
and tries to avoid re-exploration.

Another failing is that the call counts are captured by running the
benchmark tests normally and not by running them under
xunit-performance. The benchmarks have been set up so that key
portions behave identically under both scenarios, but the real
possibility exists that the call counts measure this way diverge from
the counts running under the performance harness.

It would probably be better to capture the call count data via the
normal profiling API so that a special build of the jit with this
capability is not needed (though note a special build is still needed
to get at the inline data).

### Coping with Noise

The impact of specific inlines can be elevated above the noise by
iteration -- repeatedly invoking methods in loops. This elevation
generally a manual proces and so restricts the set of inlines that can
be studied. But some degree of this is probabaly necesary.

Adoption of a benchmarking framework like xunit-perf allows for the
iteration strategy to be determined after the benchmark is authored.

Runs can be repeated with the well-known result that if the noise is
uncorrelated, the noise level will fall of with the square root of the
number of runs. However on windows at least we have seen that the
ambient noise level can vary over periods of minutes to hours. So some
kind of adaptive iteration strategy might be required where the
benchmarking harness periodically runs some known-effort workload to
assess the ambient noise, and then either records the noise level or
tries to adjust (or perhaps defer) data gathering to compensate for
higher noise levels.

There is also some inherent sampling noise. Consider this simple model
of how PMC sampling works. The per-CPU PMC is programmed with some
count-down value N. The OS then schedules processes to this
CPU. Instructions are executed and the counter counts down. When it
hits zero, the entire allotment of counts is "charged" to the current
process. Suppose during this time the process being benchmarked ran
for most of the time but for some fraction of instructions alpha,
other processes ran on the CPU. Then the expected instruction charge
to the benchmark process for this interval is:
```
   E = alpha * 0 + (1-alpha) * N
```
which reflects that on some of the PMC rollovers the process is not
charged even though it made progress, and on others it is charged but
made somewhat less progress then the charge would indicate.

The actual progress towards completion is given by the same formula.
If the entire benchmark runs for K instructions then on average during
the benchmark's execution the number of charges will be K/E, and hence
the expected value for the total charge is K, which equals the actual
total charge. So the added noise here does not apparently bias the
estimated mean. It does however, create variance.

The existence of this variance is readily observable. Unfortunately
the exact nature of this variance is not well characterized. See for
instance the discussions in
[Flater](http://nvlpubs.nist.gov/nistpubs/technicalnotes/NIST.TN.1826.pdf).
However it seems reasonble to assume that the variance increases,
perhaps nonlinearly, with increasing alpha, and also increases if
alpha itself is subject to variation, and that the variance does not
go to zero as the benchmark is run for longer intervals.

### Speed Model -- Idealized

The idealized speed model for the change in instructions retired
for a single isolated line into root at some site is:
```
  InstRetiredDelta = RootCallCount * InstRetiredPerRootCallDelta
  InstRetiredPerRootCallDelta = Overhead + CallDelta * InstRetiredPerCallDelta
  InstRetiredPerCallDelta = F(...)
```
See table below for explanation of these terms. InstRetiredDelta,
RootCallCount, InstRetiredPerRootCallDelta, Overhead, CallDelta, and
InstRetiredPerCallDelta measured after the fact.

For predictive purposes they must be derived from modelling.

### Speed Model -- Modelling

Attempts to coax workable performance models out of the data gathered
above have largely fallen flat.

The first challenge is to figure out what to model. With the current
data set, there are several viable options: 
- InstRetiredDelta
- InstRetiredPct
- InstrRetiredPerRootCallDelta
- InstrRetiredPerCallDelta

The first two measures reflect the realized potential of the inline in
some benchmark setting. They seem somewhat arbitrary -- if the
benchmark was run for twice as long, the overall change in
instructions would double as well. And the percentage value likewise
seems off -- consider a test like the CscBench that has two timed
sub-benchmarks.  If an inline benefits one and not the other, the
percentage change in instructions retired depends on the relative
number of instructions run for the two sub-benchmarks. In terms of the
idealized model, `RootCallCount` is thus something we can't easily
characterize.

So some sort of relative measure seems more appropriate. Because the
jit generally has no notion of the overall importance of the root
method in the ongoing processing (with some exceptions: the .cctor is
known to run rarely, and when/if there's profile feedback, the jit
might know something from past observations), it must presume that the
root method might be called frequently. So a plausible figure of merit
for inline benefit is the change in instructions retired per call to
the root method: `InstRetiredPerRootCallDelta`.

One could likewise argue that the right figure of merit for speed is
`InstRetiredPerCallDelta` -- the change in instructions retired per
call to the inlinee. This could be multiplied by a local estimate for
call site frequency to arrive at a projected per call benefit. The jit
computes block frequency estimates for other purposes and it would be
good if all such estimates agreed. So instead of having this be implicit
in the inline profit model, it could be made explicit.

With either of these relative measures there is still potential for
wide dynamic range as instruction retirement counts can be amplified
by loops in the root or in the callee.

Measurement of either of these requires that `RootCallCount` and
`CallDelta` be captured. This is currently done with the special
instrumentation mentioned above.

Note also that `CallDelta` may well be zero, in which case the
`InstRetiredPerRootCall` reflects just the `Overhead` term. This term
represents changes in the root that are not "in the vicinity" of the
inline site -- eg extra register saves in the prolog or epilog, or
improved or pessimized code in other parts of the root method. 

Also, the number of times `CallDelta` is observed to be zero is
overstated in the V12 data set because before call count values are
not always available (see note above about additional data in the
presence of crossgen). This should be fixed in the forthcoming V13
data set.

Unfortunately is is proving difficult to find good models for
any of the measures above. Some potential explanations:

- High noise levels. Typical noise of 0.01% - 0.1% still means variations
  on the order of 5M instructions. Many inlines will have absolute
  impact below this level.
- Missing key observations
- Errors in measurement or in post-processing
- Poor selection of benchmarks
- Varying noise levels

### Speed Model -- Modelling Attempts

Various approaches that have been tried, without success, for
performance models:

- Find some subset of the data that is predictdable. For instance 
cases with high `CallDelta`
- General linear modelling with nonlinear terms and interaction terms
- Nonlinear models like mars
- Quantile an robust regressions
- Trying to classify rather than regress, classify as "improvement" or
"regression", or some multinomial sense of "goodness".
- Transforming the response to reduce the dynamic range (~ predict log of delta)
- Temporarily allowing some output terms (eg `RootCallCount`, `CallDelta`) in models
- Ensemble models (random forest, gradient boosting). While we might not want
  to implement such a model, if they're unable to predict results well, then there
  is not much hope for simpler implementable models
- Weighted models, where the weight is used to
  - Cope with potentical heteroscedastic results
  - Ignore impact of outliers
  - Emphasize instances felt to be above the noise level

Very few models can explain more than a few percent of the variation.

### Speed Model -- Implemented Model

The model currently implemented in the `ModelPolicy` came from an
early (V3) data set, and relies on just 210 observations. It predicts
`InstRetiredPerCallDelta`. It is a penalized linear model that can
explain only about 24% of the variation.

For use in the heuristic, the speed estimate from the model is
multiplied by a local estimate of `CallDelta` to give an estimate of
`InstRetiredPerRootCallDelta`. This local estimate is ad-hoc and was
chosen to give some boost to call sites believed to be in loops in the
root method.

This version of the model was intended to be preliminary so that a
trial implementation of the ModelPolicy and idealized heuristic could
be assessed. However no better model has emerged in the time since.

## Current Heuristic

The current ModelPolicy heuristic follows the form of the idealized
heuristic.  It uses the size model and speed model, along with a local
call site weight and a size-speed tradeoff parameter. The weight and
tradeoff parameters were set based on benchmark runs and size
assessments.

Results show about a 2% geomean improvement in the CoreCLR benchmarks,
with around a 2% size decrease in the core library crossgen size, and
about a 1% throughput improvement.

Evaluation of this heuristic on other benchmarks is just beginning.
Some tests on parts of RavenDB show a possible 2% CQ improvement,
though there were some interations with force inline
directives. Measurements on ASP.Net Techempower plaintext show about
at 2% regression.

Viewed as a classifier, here's how well the implemented model does at 
implementing the idealized heuristic (V12 data):

              | Est Size Decrease | Est Profitable | Est Don't Inline | Total
--------------|-------------------|----------------|------------------|-------
Size Decrease |   2052            |       86       |     690          | 2828
Profitable    |     25            |        7       |     384          |  416
Don't Inline  |    111            |       39       |    2732          | 2882
Total         |   2188            |      132       |    3806          | 6126

Accuracy is 78% overall. The largest errors come from inlines that
actually decrease size, but are estimated to increase size, and then
judged as unprofitable (690), and from inlines that are correctly
estimated to increase size but are then assessed as unprofitable.

Note that there may be substantial labelling error for the
size-increasing cases, given the high noise levels in profitabily
measurements and the low impact of many inline instances.

## Alternatives

### Full-on Classification Model

One might legitimately ask if it would be better to try and learn the
idealized heuristic directly. Such a model would incorporate aspects
of the size and speed models, though they might no longer be
distinguishable as such. 

### Learning from Inline Forests

Instead of measuring inlines in isolation, one might attempt to infer
value by studying performance changes for entire inline forests. This
seems to match (in spirit) the approach taken in Kulkarni, et. al.  A
randomized heuristic is used, and this creates a collection of forests
and performance results. Results are projected back onto individual
inlines in the forest and, for each inline, the projected results are
aggregated into some kind of label for that inline.

For instance, one could track three numbers (possibly weighted by
magnitude of the change) for each instance: the number of times it
appears in a run that increases performance, the number of times in
appears in a run that decreases performance, and the number of times
it does not appear at all. The objective would be to then learn how to
identify inlines whos appearance is correlated with improved
performance.

### Finding Ideal Forests

Along these lines one might also use randomness or a genetic approach
to try and identify the "optimal" inline forest for each benchmark,
and then attempt to generalize from there to a good overall inline
heuristic.

## Inline Data Files

The files have a header row with column names (meanings below), and
then data rows, one per inline instance.

Column Type | Meaning                              | Use in Heuristic?
------------|--------------------------------------|-----
input       | observation available for heuristic  | Yes
estimate    | value internally derived from inputs | Maybe
meta        | metadata about the instance          | No
output      | measured result                      | No

The table below describes the V12 (and forthcoming V13) data sets.
Older files may have a subset of this data, and may contain a Version0
row for each method giving method information without any inlines.

Column Name                     | Type     | Meaning
---------------------           |----------|--------
Benchmark                       | meta     | Name of benchmark program
SubBenchmark                    | meta     | none (all sub-benchmark data now aggregated)
Method                          | meta     | Token value of the root method
Version                         | meta     | Ordinal number of this inline
HotSize                         | output   | Hot code size of method after this inline (bytes)
ColdSize                        | output   | Cold code size of method after this inline (bytes)
JitTime                         | output   | Time spent in code gen after inlining (microseconds)
SizeEstimate                    | estimate | Estimated code size for this method (hot + cold)
TimeEstmate                     | estimate | Estimated jit time for this method (microseconds)
ILSize                          | input    | Size of callee method IL buffer (bytes)
CallsiteFrequency               | estimate | Importance of the call site (factor)
InstructionCount                | input    | Number of MSIL instructions in the callee IL
LoadStoreCount                  | input    | Number of "load-store" MSIL instructions in callee IL
Depth                           | input    | Depth of this call site (1 == top-level)
BlockCount                      | input    | Number of basic blocks in the callee
Maxstack                        | input    | Maxstack value from callee method header
ArgCount                        | input    | Number of arguments to callee (from signature)
ArgNType                        | input    | Type of Nth argument (factor, CorInfoType)
ArgNSize                        | input    | Size of Nth argument (bytes)
LocalCount                      | input    | Number of locals in callee (from signature)
ReturnType                      | input    | Type of return value (factor, CorInfoType)
ReturnSize                      | input    | Size of return value (bytes)
ArgAccessCount                  | input    | Number of LDARG/STARG opcodes in callee IL
LocalAccessCount                | input    | Number of LDLOC/STLOC opcodes in callee IL
IntConstantCount                | input    | number of LDC_I and LDNULLopcodes in callee IL
FloatConstantCount              | input    | number of LDC_R opcodes in callee IL
IntLoadCount                    | input    | number of LDIND_I/U opcodes in callee IL
FloatLoadCount                  | input    | number of LDIND_R opcodes in callee IL
IntStoreCount                   | input    | number of STIND_I opcodes in callee IL
FloatStoreCount                 | input    | number of STIND_R opcodes in callee IL
SimpleMathCount                 | input    | number of ADD/SUB/.../CONV_I/U opcodes in callee IL
ComplexMathCount                | input    | number of MUL/DIV/REM/CONV_R opcodes in callee IL
OverflowMathCount               | input    | number of CONV_OVF and math_OVF opcodes in callee IL
IntArrayLoadCount               | input    | number of LDELEM_I/U opcodes in callee IL
FloatArrayLoadCount             | input    | number of LDELEM_R opcodes in callee IL
RefArrayLoadCount               | input    | number of LDELEM_REF opcodes in callee IL
StructArrayLoadCount            | input    | number of LDELEM opcodes in callee IL
IntArrayStoreCount              | input    | number of STELEM_I/U opcodes in callee IL
FloatArrayStoreCount            | input    | number of STELEM_R opcodes in callee IL
RefArrayStoreCount              | input    | number of STELEM_REF opcodes in callee IL
StructArrayStoreCount           | input    | number of STELEM opcodes in callee IL
StructOperationCount            | input    | number of *OBJ and *BLK opcodes in callee IL
ObjectModelCount                | input    | number of CASTCLASS/BOX/etc opcodes in callee IL
FieldLoadCount                  | input    | number of LDLEN/LDFLD/REFANY* in callee IL
FieldStoreCount                 | input    | number of STFLD in callee IL
StaticFieldLoadCount            | input    | number of LDSFLD in callee IL
StaticFieldStoreCount           | input    | number of STSFLD in callee IL
LoadAddressCount                | input    | number of LDLOCA/LDARGA/LD*A in callee IL
ThrowCount                      | input    | number of THROW/RETHROW in callee IL
ReturnCount                     | input    | number of RET in callee IL (new in V13)
CallCount                       | input    | number of CALL*/NEW*/JMP in callee IL
CallSiteWeight                  | estimate | numeric weight of call site
IsForceInline                   | input    | true if callee is force inline
IsInstanceCtor                  | input    | true if callee is an .ctor
IsFromPromotableValueClass      | input    | true if callee is from promotable value class
HasSimd                         | input    | true if callee has simd args/locals
LooksLikeWrapperMethod          | input    | true if callee simply wraps another call
ArgFeedsConstantTest            | input    | number of times an arg reaches compare vs constant
IsMostlyLoadStore               | input    | true if loadstore count is large fraction of instruction count
ArgFeedsRangeCheck              | input    | number of times an arg reaces compare vs ldlen
ConstantArgFeedsConstantTest    | input    | number of times a constant arg reaches a compare vs constant
CalleeNativeSizeEstimate        | estimate | LegacyPolicy's size estimate for callee (bytes * 10)
CallsiteNativeSizeEstimate      | estimate | LegacyPolicy's size estimate for "savings" from inlining (bytes * 10)
ModelCodeSizeEstimate           | estimate | ModelPolicy's size estimate (bytes * 10)
ModelPerCallInstructionEstimate | estimate | ModelPolicy's speed estimate (inst retired per call to callee)
IsClassCtor                     | input    | True if callee is a .cctor (v13)
IsSameThis                      | input    | True if callee and root are both instances with same this pointer (v13)
CallerHasNewArray               | input    | True if caller contains NEWARR operation (v13)
CallerHasNewObj                 | input    | True if caller contains NEWOBJ operation (v13)
HotSizeDelta                    | output   | Change in hot size because of this inline (bytes)
ColdSizeDelta                   | output   | Change in cold size because of this inline (bytes)
JitTimeDelta                    | output   | Change in jit time because of this inline (microseconds)
InstRetiredDelta                | output   | Change in instructions retired because of ths inline (millions)
InstRetiredSD                   | estimate | Estimated standard deviation of InstRetiredDelta (millions)
InstRetiredPct                  | output   | Percent change in instructions retired
CallDelta                       | output   | Change in number of calls to the callee because of this inline
InstRetiredPerCallDelta         | output   | InstRetiredDelta/CallDelta or 0 if CallDelta is 0
RootCallCount                   | output   | Number of calls to root method
InstRetiredPerRootCallDelta     | output   | InstRetiredDelta/RootCallCount
Confidence                      | meta     | Bootstrap confidence that this inline caused instructions retired to change

## Options for Changing Inliner Behavior

Build a release jit with -DINLINE_DATA=1. This enables the following COMPlus settings:

Setting               | Effect
----------------------|---------------
JitInlineDumpData     | dumps inline data
JitInlineDumpXml      | dumps inlines in xml format
JitInlinePolicyReplay | enable replay from replay file
JitInlineReplayFile   | name of the replay file to read from
JitInlinePolicyFull   | enable FullPolicy heuristic
JitInlinePolicyModel  | enable ModelPolicy heurisitic
JitInlineLimit        | enable K-limiting
JitNoInlineRange      | disable inlines in a subset of methods

## List of Areas for Investigation

- Improvements to Size data collection
  - Modify collection process to walk inline trees in various orders
- Improvements to the Size models
  - Analyze cases where existing size model makes poor predictions
  - Look for corrlated inputs
  - Look for inputs columns with zero variance and/or low variance, 
    and either remove them or add cases to boost their relevance
  - See if there is a better way to account for the operations done by the inlinee
- Improvements to the Speed data collection
  - Reduce noise levels (thread affinity, priority, more frequent sampling, etc)
  - Identify noisy runs and retry if warranted
  - Round-robin execution to "sample" benchmarks at different times
  - More iterations, more reruns
  - Eliminate noise entirely using instrumentation or a tool like PIN
  - Understand possible divergence between xunit-perf and regular runs
  - Get rid of need for instrumented build, use CLR profiler API instead
  - Get rid of split modelling where sometimes the program is run
    under the perf harness and other times it is run normally
  - Directly measure call site frequency rather than infer it
  - Modify collection process to walk inline trees in various orders
  - Generalize collection to work with any test program
  - Wider variety of measurements
  - Develop techniques to measure and attribute performance to
    inline ensembles to speed up collection
- Improvements to the Speed model
  - Settle on proper figure of merit: Instructions or Instructions per XXX
  - Deal with potential heteroskedacicity
- Improvements to the idealized heuristic
  - Randomized studies looking for good inlining patterns
  - Manual tuning to do likewise
  - Find way to code up oracular inliner and benchmark the heuristics that way
- Improvements to the actual heuristic
  - If local call site weight estimate needed, find good way to create one
  - If root method importance estimate needed, find good way to create one
  - Build full-model classifiers that incorporate tradeoffs
  - Automate process of tuning parameters
- Other
  - Impact of instrumented counts (IBC) on heuristics
  - Are different heuristics warranted for prejit and jit?
  - Investigate stability of models over time
  - Investigate variability of models for different OSs or ISAs


