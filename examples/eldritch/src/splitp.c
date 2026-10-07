#define TWO_ARGS
int split_params(int a
#ifdef TWO_ARGS
                 , int b
#endif
                 )
{
#ifdef TWO_ARGS
    return a * 10 + b;
#else
    return a;
#endif
}
