using Drawer.Core;
internal static class DesktopPolicyChecks
{
    public static void Run()
    {
        int n=0;
        void Check(bool condition,string label) { if(!condition) throw new Exception("FAIL desktop: "+label); n++; Console.WriteLine("PASS desktop: "+label); }
        Check(DesktopPolicy.Allowed(DesktopPolicy.Evaluate(false,false,true,false,false)),"exposed desktop trigger with restored app foreground");
        Check(DesktopPolicy.Allowed(DesktopPolicy.Evaluate(false,false,false,true,true)),"drawer remains usable after foreground changes to restored app");
        Check(!DesktopPolicy.Allowed(DesktopPolicy.Evaluate(false,false,false,true,false)),"hidden own window cannot trigger");
        Check(!DesktopPolicy.Allowed(DesktopPolicy.Evaluate(false,false,false,false,false)),"application covering trigger blocks opening");
        Check(!DesktopPolicy.Allowed(DesktopPolicy.Evaluate(true,false,true,false,false)),"maximized foreground blocks exposed edge sliver");
        Check(!DesktopPolicy.Allowed(DesktopPolicy.Evaluate(true,false,false,true,true)),"maximized foreground closes drawer");
        Check(!DesktopPolicy.Allowed(DesktopPolicy.Evaluate(false,true,true,false,false)),"fullscreen blocks desktop trigger");
        Check(!DesktopPolicy.Allowed(DesktopPolicy.Evaluate(false,true,false,true,true)),"fullscreen blocks visible drawer");
        var model=new DrawerInteraction(); double time=0;
        void Step(double ms,string gate,bool trigger,bool inside) { for(double t=0;t<ms;t+=10) {time+=10;model.Update(time,.01,DesktopPolicy.Allowed(gate),trigger,inside);} }
        Step(800,"desktop",true,false); Check(model.Position==1,"exposed edge opens after dwell");
        Step(2000,"drawer-hit",false,true); Check(model.Position==1 && model.TargetOpen,"foreground transition while pointer in drawer no longer resets");
        model.Reset(); Step(100,"desktop",true,false); Check(!model.TargetOpen,"shell hide resets dwell");
        Step(700,"desktop",true,false); Check(model.Position==1,"edge can reopen after shell hide");
        Step(500,"foreground-maximized",true,true); Check(model.State=="Hidden","maximized application still closes immediately");
        Console.WriteLine($"ALL {n} DESKTOP POLICY CHECKS PASSED");
    }
}
