// Probe: many C++ call/reference shapes, each reaching a UNIQUELY-NAMED helper. `entry` is the only
// root. Carve --prune, then g++-compile the carved output: any helper dropped = its call shape isn't
// recognized as a reference (the class the template-arg bug belonged to). Every helper is reachable
// from entry, so a sound carve keeps them all and the carved file compiles.
#include <functional>
#include <memory>

int h_plain(int x)        { return x + 1; }   // plain call
int h_ptr_target(int x)   { return x + 2; }   // via function pointer
int h_operator(int x)     { return x + 3; }   // via overloaded operator body
int h_lambda(int x)       { return x + 4; }   // called inside a lambda
int h_stdfunction(int x)  { return x + 5; }   // assigned to std::function
int h_ternary(int x)      { return x + 6; }   // in a ternary
int h_default_arg(int x)  { return x + 7; }   // used as a default argument
int h_member(int x)       { return x + 8; }   // called from a method
int h_arrow(int x)        { return x + 9; }   // via ptr->method chain
int h_static(int x)       { return x + 10; }  // via Class::static_method
int h_ctor(int x)         { return x + 11; }  // from a constructor body
int h_conv(int x)         { return x + 12; }  // from a conversion operator
int h_new_expr(int x)     { return x + 13; }  // in a new-expression arg

struct Op { int v; Op operator+(const Op& o) const { return { h_operator(v + o.v) }; } };

struct Arrow { template<int N> int amt(int x) const { return h_arrow(x) + N; } };  // ptr->method<N>()

struct Widget {
    int base;
    Widget(int x) : base(h_ctor(x)) {}
    int via_member(int x) const { return h_member(x); }
    static int via_static(int x) { return h_static(x); }
    operator int() const { return h_conv(base); }
};

int call_default(int x, int y = 0);                 // decl with default in def below
int call_default(int x, int y) { return x + y; }

int entry(Widget* w) {
    int acc = h_plain(1);
    int (*fp)(int) = &h_ptr_target;   acc += fp(2);
    Op a{3}, b{4};                    acc += (a + b).v;
    auto lam = [](int x){ return h_lambda(x); };   acc += lam(5);
    std::function<int(int)> f = h_stdfunction;      acc += f(6);
    acc += (acc > 0 ? h_ternary(7) : 0);
    acc += call_default(h_default_arg(8));
    acc += w->via_member(9);
    Widget* p = w;                    acc += p->via_member(10);
    acc += Widget::via_static(11);
    Widget mk(12);                    acc += static_cast<int>(mk);   // conversion operator
    int* np = new int(h_new_expr(13)); acc += *np; delete np;
    Arrow ar; Arrow* ap = &ar;         acc += ap->amt<3>(14);   // arrow + explicit template args
    return acc;
}
