// DSP helpers (C++ templates in a namespace). dsp::smooth<N> is instantiated from
// TempSensor::sample() as `smooth<4>(x)`. That explicit template-argument form parses as a
// template_function call, not a bare identifier — the front-end matches it specifically, or the
// template would be pruned and the carved tree wouldn't compile.
#ifndef FILTER_HPP
#define FILTER_HPP

namespace dsp {

template <int N>
int smooth(int x)
{
    int acc = 0;
    for (int i = 0; i < N; ++i)
        acc += x;
    return acc / N;
}

} // namespace dsp

#endif // FILTER_HPP
