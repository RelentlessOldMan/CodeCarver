/* Low-level HAL (C). Called from the C++ sensors — the C++ -> C edge of the cross-language carve. */
#ifndef HAL_H
#define HAL_H

#ifdef __cplusplus
extern "C" {
#endif

int hal_read_adc(int channel);

#ifdef __cplusplus
}
#endif

#endif /* HAL_H */
