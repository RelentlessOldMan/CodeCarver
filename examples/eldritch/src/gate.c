#define BEGIN_FN(name) int name(void) {
#define END_FN }
int gate_helper(void);
BEGIN_FN(gate_fn)
    return gate_helper() + 1;
END_FN
